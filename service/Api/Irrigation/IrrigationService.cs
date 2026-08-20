using System.Text.Json;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class IrrigationService
{
    private readonly IrrigationConfiguration _options;
    private readonly OpenMeteoForecastClient _forecast;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IrrigationState _state;
    private ForecastSummary? _cachedForecast;
    private DateTimeOffset _forecastRefreshAfter;
    private DateTimeOffset _forecastValidUntil;

    public IrrigationService(IConfiguration configuration, OpenMeteoForecastClient forecast)
    {
        _options = configuration.GetSection("Api:IrrigationConfiguration").Get<IrrigationConfiguration>() ?? new();
        _forecast = forecast;
        _state = LoadState();

        foreach (var group in GetConfiguredGroups())
            _state.BalanceDeficitMm.TryAdd(group.Key, 0);

        CleanupIrrigationRuns(DateTimeOffset.UtcNow);
        WarnAboutInconsistentBalanceGroups();
    }

    public WeatherObservation? GetLatestObservation() =>
        _state.Observations.OrderByDescending(x => x.Timestamp).FirstOrDefault();

    public async Task AddObservationAsync(WeatherObservation observation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var receivedAt = DateTimeOffset.UtcNow;
            var timestamp = observation.Timestamp ?? receivedAt;
            var allowedSkew = TimeSpan.FromMinutes(Math.Max(0, _options.MaximumTimestampSkewMinutes));
            if (timestamp < receivedAt - allowedSkew || timestamp > receivedAt + allowedSkew)
                timestamp = receivedAt;

            _state.Observations.Add(observation with
            {
                Timestamp = timestamp,
                HumidityPct = Math.Clamp(observation.HumidityPct, 0, 100),
                RainfallMm = Math.Max(0, observation.RainfallMm)
            });

            AccumulateCompletedDays(receivedAt);
            _state.Observations.RemoveAll(x => (x.Timestamp ?? DateTimeOffset.MinValue) < receivedAt.AddDays(-8));
            CleanupIrrigationRuns(receivedAt);
            await PersistAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RecordIrrigationAsync(
        string zoneId,
        int runtimeSeconds,
        string eventId,
        string type,
        CancellationToken cancellationToken)
    {
        if (runtimeSeconds <= 0 || string.IsNullOrWhiteSpace(eventId)) return false;

        var zone = _options.Zones.FirstOrDefault(x => string.Equals(x.Id, zoneId, StringComparison.OrdinalIgnoreCase));
        if (zone is null || zone.ApplicationRateMmPerHour <= 0) return false;

        var normalizedType = NormalizeRunType(type);
        if (normalizedType is null) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Loxone/HTTP may retry. An event id is accepted only once for the 30-day history window.
            if (_state.IrrigationRuns.Any(x => string.Equals(x.EventId, eventId, StringComparison.OrdinalIgnoreCase)))
                return true;

            var endedAt = DateTimeOffset.UtcNow;
            var startedAt = endedAt.AddSeconds(-runtimeSeconds);
            var appliedMm = runtimeSeconds / 3600.0 * zone.ApplicationRateMmPerHour;
            var group = ResolveBalanceGroup(zone);

            // Rinse/manual runs are recorded for history but do not alter the irrigation water balance.
            if (string.Equals(normalizedType, "Irrigation", StringComparison.OrdinalIgnoreCase))
            {
                _state.BalanceDeficitMm.TryGetValue(group, out var current);
                _state.BalanceDeficitMm[group] = Math.Max(0, current - appliedMm);
            }

            _state.IrrigationRuns.Add(new IrrigationRun(
                eventId,
                zone.Id,
                group,
                normalizedType,
                runtimeSeconds,
                Math.Round(appliedMm, 3),
                startedAt,
                endedAt));

            CleanupIrrigationRuns(endedAt);
            await PersistAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<IrrigationRun>> GetIrrigationRunsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var changed = CleanupIrrigationRuns(DateTimeOffset.UtcNow);
            if (changed) await PersistAsync(cancellationToken);
            return _state.IrrigationRuns.OrderByDescending(x => x.EndedAt).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IrrigationResult> CalculateAsync(CancellationToken cancellationToken)
    {
        List<WeatherObservation> observations;
        Dictionary<string, double> balances;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var changed = AccumulateCompletedDays(DateTimeOffset.UtcNow);
            if (changed) await PersistAsync(cancellationToken);
            observations = [.. _state.Observations];
            balances = new Dictionary<string, double>(_state.BalanceDeficitMm, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }

        var now = DateTimeOffset.UtcNow;
        observations = observations.Where(x => x.Timestamp is not null).OrderBy(x => x.Timestamp).ToList();
        var cutoff24 = now.AddHours(-24);
        var cutoff72 = now.AddHours(-72);
        var last24 = observations.Where(x => x.Timestamp >= cutoff24).ToList();
        var rain24 = RainDelta(WindowWithBaseline(observations, cutoff24));
        var rain72 = RainDelta(WindowWithBaseline(observations, cutoff72));
        var localDataComplete = HasComplete24HourCoverage(last24, now, _options);
        var et0 = CalculateEt0(last24, _options);
        var forecast = await GetForecastAsync(cancellationToken);
        var effectiveForecastRain = forecast.RainMm * _options.EffectiveRainFactor * _options.ForecastRainWeight;

        var zoneResults = new List<IrrigationZoneResult>();
        foreach (var group in GetConfiguredGroups())
        {
            balances.TryGetValue(group.Key, out var accumulated);
            var required = Math.Max(0, accumulated - effectiveForecastRain);
            var shouldRun = required >= _options.IrrigationTriggerMm;
            var totalRate = group.Sum(x => Math.Max(0, x.ApplicationRateMmPerHour));
            var runtime = shouldRun && totalRate > 0
                ? (int)Math.Round(required / totalRate * 3600)
                : 0;
            runtime = Math.Clamp(runtime, 0, Math.Max(0, _options.MaximumZoneRuntimeSeconds));

            // All circuits in one balance group run the same duration. Their physical application
            // rates add together to replenish the shared root zone.
            foreach (var zone in group)
                zoneResults.Add(new IrrigationZoneResult(zone.Id, group.Key, Math.Round(required, 2), runtime));
        }

        var maxDeficit = balances.Count == 0 ? 0 : balances.Values.Max();
        return new IrrigationResult(
            zoneResults.Any(x => x.RuntimeSeconds > 0),
            localDataComplete,
            Math.Round(et0, 2),
            Math.Round(rain24, 2),
            Math.Round(rain72, 2),
            Math.Round(forecast.RainMm, 2),
            Math.Round(forecast.Et0Mm, 2),
            Math.Round(maxDeficit, 2),
            zoneResults,
            now);
    }

    private bool AccumulateCompletedDays(DateTimeOffset nowUtc)
    {
        var tz = GetTimeZone();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime);
        var ordered = _state.Observations.Where(x => x.Timestamp is not null).OrderBy(x => x.Timestamp).ToList();
        var firstObservation = ordered.FirstOrDefault();
        if (firstObservation?.Timestamp is null) return false;

        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(firstObservation.Timestamp.Value, tz).DateTime);
        var next = _state.LastBalancedLocalDate?.AddDays(1) ?? firstDate;
        var changed = false;

        while (next < today)
        {
            var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(next.ToDateTime(TimeOnly.MinValue), tz), TimeSpan.Zero);
            var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(next.AddDays(1).ToDateTime(TimeOnly.MinValue), tz), TimeSpan.Zero);
            var dayObs = ordered.Where(x => x.Timestamp >= startUtc && x.Timestamp < endUtc).ToList();
            var coverageGap = TimeSpan.FromMinutes(Math.Max(1, _options.CompletedDayEdgeGapMinutes));

            if (HasCompleteDayCoverage(dayObs, startUtc, endUtc, coverageGap))
            {
                var et0 = CalculateEt0(dayObs, _options);
                var effectiveRain = RainForPeriod(ordered, startUtc, endUtc) * _options.EffectiveRainFactor;

                foreach (var group in GetConfiguredGroups())
                {
                    _state.BalanceDeficitMm.TryGetValue(group.Key, out var current);
                    var exposure = Math.Max(0, group.First().Exposure);
                    var nextDeficit = Math.Max(0, current + et0 * exposure - effectiveRain);
                    _state.BalanceDeficitMm[group.Key] = Math.Min(Math.Max(0, _options.MaximumDeficitMm), nextDeficit);
                }
            }
            else
            {
                Log.Warning("Skipping irrigation water-balance day {date}: local weather coverage is incomplete", next);
            }

            _state.LastBalancedLocalDate = next;
            next = next.AddDays(1);
            changed = true;
        }

        return changed;
    }

    private async Task<ForecastSummary> GetForecastAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedForecast is not null && now < _forecastRefreshAfter) return _cachedForecast;

        try
        {
            _cachedForecast = await _forecast.GetForecastAsync(cancellationToken);
            _forecastRefreshAfter = now.AddMinutes(30);
            _forecastValidUntil = now.AddHours(Math.Max(1, _options.ForecastHours));
            return _cachedForecast;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to retrieve irrigation forecast; continuing without forecast adjustment");
            if (_cachedForecast is not null && now < _forecastValidUntil) return _cachedForecast;
            _cachedForecast = null;
            _forecastRefreshAfter = DateTimeOffset.MinValue;
            _forecastValidUntil = DateTimeOffset.MinValue;
            return new ForecastSummary(0, 0);
        }
    }

    private IEnumerable<IGrouping<string, IrrigationZoneConfiguration>> GetConfiguredGroups() =>
        _options.Zones.GroupBy(ResolveBalanceGroup, StringComparer.OrdinalIgnoreCase);

    internal static string ResolveBalanceGroup(IrrigationZoneConfiguration zone) =>
        string.IsNullOrWhiteSpace(zone.BalanceGroup) ? zone.Id : zone.BalanceGroup.Trim();

    private void WarnAboutInconsistentBalanceGroups()
    {
        foreach (var group in GetConfiguredGroups())
        {
            var exposures = group.Select(x => Math.Round(x.Exposure, 6)).Distinct().ToArray();
            if (exposures.Length > 1)
                Log.Warning("Irrigation balance group {group} contains different exposure values; using the first zone's exposure", group.Key);
        }
    }

    private TimeZoneInfo GetTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId); }
        catch
        {
            Log.Warning("Unknown irrigation timezone {timezone}; using UTC", _options.TimeZoneId);
            return TimeZoneInfo.Utc;
        }
    }

    private static string? NormalizeRunType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Irrigation";
        if (type.Equals("Irrigation", StringComparison.OrdinalIgnoreCase)) return "Irrigation";
        if (type.Equals("Rinse", StringComparison.OrdinalIgnoreCase)) return "Rinse";
        if (type.Equals("Manual", StringComparison.OrdinalIgnoreCase)) return "Manual";
        return null;
    }

    private bool CleanupIrrigationRuns(DateTimeOffset now) =>
        _state.IrrigationRuns.RemoveAll(x => x.EndedAt < now.AddDays(-30)) > 0;

    internal static bool HasCompleteDayCoverage(
        IReadOnlyList<WeatherObservation> observations,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan edgeGap)
    {
        if (observations.Count < 2) return false;
        var first = observations[0].Timestamp ?? DateTimeOffset.MaxValue;
        var last = observations[^1].Timestamp ?? DateTimeOffset.MinValue;
        return first <= start + edgeGap && last >= end - edgeGap;
    }

    internal static double RainForPeriod(
        IReadOnlyList<WeatherObservation> orderedObservations,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        var window = new List<WeatherObservation>();
        var baseline = orderedObservations.LastOrDefault(x => x.Timestamp < start);
        if (baseline is not null) window.Add(baseline);
        window.AddRange(orderedObservations.Where(x => x.Timestamp >= start && x.Timestamp < end));
        return RainDelta(window);
    }

    private static List<WeatherObservation> WindowWithBaseline(
        IReadOnlyList<WeatherObservation> observations,
        DateTimeOffset cutoff)
    {
        var window = new List<WeatherObservation>();
        var baseline = observations.LastOrDefault(x => x.Timestamp < cutoff);
        if (baseline is not null) window.Add(baseline);
        window.AddRange(observations.Where(x => x.Timestamp >= cutoff));
        return window;
    }

    private static bool HasComplete24HourCoverage(
        IReadOnlyList<WeatherObservation> observations,
        DateTimeOffset now,
        IrrigationConfiguration options)
    {
        if (observations.Count < 2) return false;
        var edgeGap = TimeSpan.FromMinutes(Math.Max(1, options.MaximumObservationEdgeGapMinutes));
        return observations[0].Timestamp <= now.AddHours(-24) + edgeGap &&
               observations[^1].Timestamp >= now - edgeGap;
    }

    internal static double RainDelta(IReadOnlyList<WeatherObservation> observations)
    {
        if (observations.Count < 2) return 0;
        double total = 0;
        for (var i = 1; i < observations.Count; i++)
        {
            var delta = observations[i].RainfallMm - observations[i - 1].RainfallMm;
            total += delta >= 0 ? delta : observations[i].RainfallMm;
        }
        return total;
    }

    internal static double CalculateEt0(IReadOnlyList<WeatherObservation> observations, IrrigationConfiguration options)
    {
        if (observations.Count < 2) return 0;
        var tMin = observations.Min(x => x.TemperatureC);
        var tMax = observations.Max(x => x.TemperatureC);
        var tMean = observations.Average(x => x.TemperatureC);
        var ea = observations.Average(x => SaturationVapourPressure(x.TemperatureC) * x.HumidityPct / 100.0);
        var es = (SaturationVapourPressure(tMin) + SaturationVapourPressure(tMax)) / 2.0;
        var vpd = Math.Max(0, es - ea);
        var pressureKpa = observations.Average(x => x.PressureHpa) / 10.0;
        var gamma = 0.000665 * pressureKpa;
        var delta = 4098 * SaturationVapourPressure(tMean) / Math.Pow(tMean + 237.3, 2);
        var wind2m = observations.Average(x => x.WindSpeedKmh) / 3.6;
        var rs = IntegrateSolarMjM2(observations, options.LuxPerWattM2);
        var day = observations[^1].Timestamp?.DayOfYear ?? DateTimeOffset.UtcNow.DayOfYear;
        var ra = ExtraterrestrialRadiation(options.Latitude, day);
        var rso = (0.75 + 2e-5 * options.ElevationM) * ra;
        var rns = 0.77 * rs;
        const double sigma = 4.903e-9;
        var cloud = rso > 0 ? Math.Clamp(1.35 * Math.Clamp(rs / rso, 0, 1) - 0.35, 0, 1) : 0;
        var rnl = sigma * (Math.Pow(tMax + 273.16, 4) + Math.Pow(tMin + 273.16, 4)) / 2.0 *
                  (0.34 - 0.14 * Math.Sqrt(Math.Max(0, ea))) * cloud;
        var rn = Math.Max(0, rns - rnl);
        var numerator = 0.408 * delta * rn + gamma * (900 / (tMean + 273)) * wind2m * vpd;
        var denominator = delta + gamma * (1 + 0.34 * wind2m);
        return denominator > 0 ? Math.Max(0, numerator / denominator) : 0;
    }

    private static double SaturationVapourPressure(double t) =>
        0.6108 * Math.Exp(17.27 * t / (t + 237.3));

    private static double IntegrateSolarMjM2(IReadOnlyList<WeatherObservation> observations, double luxPerWattM2)
    {
        if (luxPerWattM2 <= 0) return 0;
        double joules = 0;
        for (var i = 1; i < observations.Count; i++)
        {
            var seconds = ((observations[i].Timestamp ?? DateTimeOffset.MinValue) -
                           (observations[i - 1].Timestamp ?? DateTimeOffset.MinValue)).TotalSeconds;
            if (seconds <= 0 || seconds > 1800) continue;
            var w1 = Math.Max(0, observations[i - 1].LightLux / luxPerWattM2);
            var w2 = Math.Max(0, observations[i].LightLux / luxPerWattM2);
            joules += (w1 + w2) / 2.0 * seconds;
        }
        return joules / 1_000_000.0;
    }

    private static double ExtraterrestrialRadiation(double latitudeDegrees, int dayOfYear)
    {
        var phi = latitudeDegrees * Math.PI / 180.0;
        var dr = 1 + 0.033 * Math.Cos(2 * Math.PI / 365 * dayOfYear);
        var sd = 0.409 * Math.Sin(2 * Math.PI / 365 * dayOfYear - 1.39);
        var ws = Math.Acos(Math.Clamp(-Math.Tan(phi) * Math.Tan(sd), -1, 1));
        const double gsc = 0.0820;
        return 24 * 60 / Math.PI * gsc * dr *
               (ws * Math.Sin(phi) * Math.Sin(sd) + Math.Cos(phi) * Math.Cos(sd) * Math.Sin(ws));
    }

    private IrrigationState LoadState()
    {
        try
        {
            if (!File.Exists(_options.StateFile)) return new IrrigationState();
            return JsonSerializer.Deserialize<IrrigationState>(File.ReadAllText(_options.StateFile)) ?? new IrrigationState();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to load irrigation state from {file}", _options.StateFile);
            return new IrrigationState();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.StateFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = _options.StateFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_state), cancellationToken);
        File.Move(temp, _options.StateFile, true);
    }
}
