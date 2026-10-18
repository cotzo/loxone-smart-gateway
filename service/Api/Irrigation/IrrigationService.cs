using System.Text.Json;
using System.Diagnostics;
using loxone.smart.gateway.Api.Gateway;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class IrrigationService
{
    private IrrigationConfiguration _options;
    private readonly OpenMeteoForecastClient _forecast;
    private readonly IrrigationMetrics _metrics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IrrigationState _state;
    private ForecastSummary? _cachedForecast;
    private bool _forecastAvailable;
    private DateTimeOffset _forecastRefreshAfter;
    private DateTimeOffset _forecastValidUntil;

    public IrrigationService(GatewayStateStore settingsStore, OpenMeteoForecastClient forecast, IrrigationMetrics? metrics = null)
    {
        _options = settingsStore.GetSnapshot().Settings.Irrigation;
        _forecast = forecast;
        _metrics = metrics ?? new IrrigationMetrics();
        _state = LoadState();

        foreach (var group in GetConfiguredGroups())
            _state.BalanceDeficitMm.TryAdd(group.Key, 0);

        CleanupIrrigationRuns(DateTimeOffset.UtcNow);
        WarnAboutInconsistentBalanceGroups();
        _metrics.UpdateStoredData(new IrrigationStoredData(_state.Observations, _state.BalanceDeficitMm,
            _state.LastBalancedLocalDate, _state.IrrigationRuns, _state.Observations.Count));
    }

    public void UpdateConfiguration(IrrigationConfiguration configuration) =>
        Volatile.Write(ref _options, CloneConfiguration(configuration));

    public IrrigationConfiguration GetConfigurationSnapshot() =>
        CloneConfiguration(Volatile.Read(ref _options));

    public async Task<IrrigationStoredData> GetStoredDataAsync(CancellationToken cancellationToken, int observationLimit = 100, int runLimit = 50)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (CleanupIrrigationRuns(now))
                await PersistAsync(cancellationToken);
            var observations = _state.Observations.OrderBy(x => x.Timestamp).ToList();
            var runs = _state.IrrigationRuns.OrderByDescending(x => x.EndedAt).ToList();
            var stored = new IrrigationStoredData(
                observationLimit <= 0 ? observations : observations.TakeLast(observationLimit).ToList(),
                new Dictionary<string, double>(_state.BalanceDeficitMm, StringComparer.OrdinalIgnoreCase),
                _state.LastBalancedLocalDate,
                runLimit <= 0 ? runs : runs.Take(runLimit).ToList(),
                observations.Count);
            _metrics.UpdateStoredData(new IrrigationStoredData(_state.Observations, _state.BalanceDeficitMm,
                _state.LastBalancedLocalDate, _state.IrrigationRuns, _state.Observations.Count));
            return stored;
        }
        finally
        {
            _gate.Release();
        }
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
            var options = Volatile.Read(ref _options);
            var allowedSkew = TimeSpan.FromMinutes(Math.Max(0, options.MaximumTimestampSkewMinutes));
            if (timestamp < receivedAt - allowedSkew || timestamp > receivedAt + allowedSkew)
                timestamp = receivedAt;

            var previousTimestamp = _state.Observations.Select(x => x.Timestamp).Where(x => x is not null)
                .Select(x => x!.Value).DefaultIfEmpty().Max();
            var previousObservation = _state.Observations
                .Where(x => x.Timestamp is not null && x.Timestamp <= timestamp)
                .OrderByDescending(x => x.Timestamp).FirstOrDefault();
            var maxRainGap = TimeSpan.FromMinutes(Math.Max(1, options.MowingMaximumDryingGapMinutes));
            var rainfallTimingUncertain = previousObservation is not null && IsRainfallTimingUncertain(
                previousObservation, observation with { Timestamp = timestamp }, maxRainGap);
            _state.Observations.Add(observation with
            {
                Timestamp = timestamp,
                HumidityPct = Math.Clamp(observation.HumidityPct, 0, 100),
                RainfallMm = Math.Max(0, observation.RainfallMm),
                RainfallTimingUncertain = observation.RainfallTimingUncertain || rainfallTimingUncertain
            });

            AccumulateCompletedDays(receivedAt);
            _state.Observations.RemoveAll(x => (x.Timestamp ?? DateTimeOffset.MinValue) < receivedAt.AddDays(-8));
            CleanupIrrigationRuns(receivedAt);
            await PersistAsync(cancellationToken);
            _metrics.RecordObservation(timestamp, previousTimestamp == default ? null : previousTimestamp, _state.Observations.Count);
            _metrics.UpdateStoredData(new IrrigationStoredData(_state.Observations, _state.BalanceDeficitMm,
                _state.LastBalancedLocalDate, _state.IrrigationRuns, _state.Observations.Count));
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
        CancellationToken cancellationToken)
    {
        if (runtimeSeconds <= 0 || string.IsNullOrWhiteSpace(eventId)) return false;

        var zone = _options.Zones.FirstOrDefault(x => string.Equals(x.Id, zoneId, StringComparison.OrdinalIgnoreCase));
        if (zone is null || zone.ApplicationRateMmPerHour <= 0) return false;

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

            _state.BalanceDeficitMm.TryGetValue(group, out var current);
            _state.BalanceDeficitMm[group] = Math.Max(0, current - appliedMm);

            _state.IrrigationRuns.Add(new IrrigationRun(
                eventId,
                zone.Id,
                group,
                "Irrigation",
                runtimeSeconds,
                Math.Round(appliedMm, 3),
                startedAt,
                endedAt));

            CleanupIrrigationRuns(endedAt);
            await PersistAsync(cancellationToken);
            _metrics.RecordRun(runtimeSeconds, appliedMm);
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
        var calculationTimer = Stopwatch.StartNew();
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

        var options = Volatile.Read(ref _options);
        var now = DateTimeOffset.UtcNow;
        observations = observations
            .Where(x => x.Timestamp is not null && x.Timestamp <= now.AddMinutes(Math.Max(0, options.MaximumTimestampSkewMinutes)))
            .OrderBy(x => x.Timestamp)
            .ToList();
        var cutoff24 = now.AddHours(-24);
        var cutoff72 = now.AddHours(-72);
        var last24 = observations.Where(x => x.Timestamp >= cutoff24).ToList();
        var rain24 = RainDelta(WindowWithBaseline(observations, cutoff24));
        var rain72 = RainDelta(WindowWithBaseline(observations, cutoff72));
        var localDataComplete = HasComplete24HourCoverage(last24, now, options);
        var et0 = CalculateEt0(last24, options);
        var forecast = await GetForecastAsync(cancellationToken);
        var effectiveForecastRain = forecast.RainMm * options.EffectiveRainFactor * options.ForecastRainWeight;

        var zoneResults = new List<IrrigationZoneResult>();
        foreach (var group in options.Zones.GroupBy(ResolveBalanceGroup, StringComparer.OrdinalIgnoreCase))
        {
            balances.TryGetValue(group.Key, out var accumulated);
            var required = Math.Max(0, accumulated - effectiveForecastRain);
            var shouldRun = required >= options.IrrigationTriggerMm;
            var totalRate = group.Sum(x => Math.Max(0, x.ApplicationRateMmPerHour));
            var calculatedRuntime = shouldRun && totalRate > 0
                ? (int)Math.Round(required / totalRate * 3600)
                : 0;
            var runtime = Math.Clamp(calculatedRuntime, 0, Math.Max(0, options.MaximumZoneRuntimeSeconds));
            var capped = runtime < calculatedRuntime;
            var summary = !shouldRun
                ? $"Required water {required:F2} mm is below the {options.IrrigationTriggerMm:F2} mm trigger."
                : totalRate <= 0
                    ? "No positive application rate is configured for this balance group."
                    : capped
                        ? $"Required water {required:F2} mm meets the trigger; calculated runtime {calculatedRuntime} s is capped at {runtime} s."
                        : $"Required water {required:F2} mm meets the trigger; {totalRate:F2} mm/h combined application rate gives {runtime} s runtime.";

            // All circuits in one balance group run the same duration. Their physical application
            // rates add together to replenish the shared root zone.
            foreach (var zone in group)
                zoneResults.Add(new IrrigationZoneResult(
                    zone.Id, group.Key, Math.Round(required, 2), runtime,
                    Math.Round(accumulated, 2), Math.Round(effectiveForecastRain, 2),
                    options.IrrigationTriggerMm, Math.Round(totalRate, 2), calculatedRuntime,
                    capped, shouldRun, summary));
        }

        var maxDeficit = balances.Count == 0 ? 0 : balances.Values.Max();
        var irrigate = zoneResults.Any(x => x.RuntimeSeconds > 0);
        var decisionSummary = irrigate
            ? $"Watering is recommended for {zoneResults.Count(x => x.RuntimeSeconds > 0)} zone(s)."
            : zoneResults.Count == 0
                ? "No irrigation zones are configured."
                : "No zones currently require watering at the configured trigger.";
        var result = new IrrigationResult(
            irrigate,
            localDataComplete,
            Math.Round(et0, 2),
            Math.Round(rain24, 2),
            Math.Round(rain72, 2),
            Math.Round(forecast.RainMm, 2),
            Math.Round(forecast.Et0Mm, 2),
            Math.Round(maxDeficit, 2),
            zoneResults,
            now,
            Math.Round(effectiveForecastRain, 2),
            _forecastAvailable,
            decisionSummary);
        calculationTimer.Stop();
        _metrics.RecordDecision(result, calculationTimer.Elapsed.TotalMilliseconds);
        return result;
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
            var options = Volatile.Read(ref _options);
            _cachedForecast = await _forecast.GetForecastAsync(options, cancellationToken);
            _forecastAvailable = true;
            _forecastRefreshAfter = now.AddMinutes(30);
            _forecastValidUntil = now.AddHours(Math.Max(1, options.ForecastHours));
            return _cachedForecast;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to retrieve irrigation forecast; continuing without forecast adjustment");
            if (_cachedForecast is not null && now < _forecastValidUntil)
            {
                _forecastAvailable = true;
                return _cachedForecast;
            }
            _cachedForecast = null;
            _forecastAvailable = false;
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

    internal static bool IsRainfallTimingUncertain(WeatherObservation previous, WeatherObservation current, TimeSpan maximumGap) =>
        previous.Timestamp is null || current.Timestamp is null ||
        current.Timestamp <= previous.Timestamp ||
        current.Timestamp.Value - previous.Timestamp.Value > maximumGap ||
        current.RainfallMm < previous.RainfallMm;

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
        var stateFile = Volatile.Read(ref _options).StateFile;
        try
        {
            if (!File.Exists(stateFile)) return new IrrigationState();
            return JsonSerializer.Deserialize<IrrigationState>(File.ReadAllText(stateFile)) ?? new IrrigationState();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to load irrigation state from {file}", stateFile);
            return new IrrigationState();
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var stateFile = Volatile.Read(ref _options).StateFile;
        var directory = Path.GetDirectoryName(stateFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temp = stateFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_state), cancellationToken);
        File.Move(temp, stateFile, true);
    }

    private static IrrigationConfiguration CloneConfiguration(IrrigationConfiguration value) =>
        JsonSerializer.Deserialize<IrrigationConfiguration>(JsonSerializer.Serialize(value)) ?? new();
}
