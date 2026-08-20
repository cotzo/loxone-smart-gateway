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
        foreach (var zone in _options.Zones) _state.ZoneDeficitMm.TryAdd(zone.Id, 0);
        CleanupIrrigationRuns(DateTimeOffset.UtcNow);
    }

    public WeatherObservation? GetLatestObservation() => _state.Observations.OrderByDescending(x => x.Timestamp).FirstOrDefault();

    public async Task AddObservationAsync(WeatherObservation observation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var receivedAt = DateTimeOffset.UtcNow;
            var timestamp = observation.Timestamp ?? receivedAt;
            var allowedSkew = TimeSpan.FromMinutes(Math.Max(0, _options.MaximumTimestampSkewMinutes));
            if (timestamp < receivedAt - allowedSkew || timestamp > receivedAt + allowedSkew) timestamp = receivedAt;
            var normalized = observation with { Timestamp = timestamp, HumidityPct = Math.Clamp(observation.HumidityPct, 0, 100), RainfallMm = Math.Max(0, observation.RainfallMm) };
            _state.Observations.Add(normalized);
            AccumulateCompletedDays(receivedAt);
            _state.Observations.RemoveAll(x => (x.Timestamp ?? DateTimeOffset.MinValue) < receivedAt.AddDays(-8));
            CleanupIrrigationRuns(receivedAt);
            await PersistAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RecordIrrigationAsync(string zoneId, int runtimeSeconds, CancellationToken cancellationToken)
    {
        if (runtimeSeconds <= 0) return false;
        var zone = _options.Zones.FirstOrDefault(x => string.Equals(x.Id, zoneId, StringComparison.OrdinalIgnoreCase));
        if (zone is null || zone.ApplicationRateMmPerHour <= 0) return false;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var appliedMm = runtimeSeconds / 3600.0 * zone.ApplicationRateMmPerHour;
            _state.ZoneDeficitMm.TryGetValue(zone.Id, out var current);
            _state.ZoneDeficitMm[zone.Id] = Math.Max(0, current - appliedMm);
            _state.IrrigationRuns.Add(new IrrigationRun(zone.Id, runtimeSeconds, Math.Round(appliedMm, 3), now));
            CleanupIrrigationRuns(now);
            await PersistAsync(cancellationToken);
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<IrrigationRun>> GetIrrigationRunsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CleanupIrrigationRuns(DateTimeOffset.UtcNow);
            return _state.IrrigationRuns.OrderByDescending(x => x.Timestamp).ToList();
        }
        finally { _gate.Release(); }
    }

    private void CleanupIrrigationRuns(DateTimeOffset now) => _state.IrrigationRuns.RemoveAll(x => x.Timestamp < now.AddDays(-30));

    public async Task<IrrigationResult> CalculateAsync(CancellationToken cancellationToken)
    {
        List<WeatherObservation> observations; Dictionary<string, double> balances;
        await _gate.WaitAsync(cancellationToken);
        try { AccumulateCompletedDays(DateTimeOffset.UtcNow); observations = [.. _state.Observations]; balances = new(_state.ZoneDeficitMm, StringComparer.OrdinalIgnoreCase); }
        finally { _gate.Release(); }
        var now = DateTimeOffset.UtcNow; observations = observations.Where(x => x.Timestamp is not null).OrderBy(x => x.Timestamp).ToList();
        var cutoff24 = now.AddHours(-24); var cutoff72 = now.AddHours(-72); var last24 = observations.Where(x => x.Timestamp >= cutoff24).ToList();
        var rain24 = RainDelta(WindowWithBaseline(observations, cutoff24)); var rain72 = RainDelta(WindowWithBaseline(observations, cutoff72));
        var localDataComplete = HasComplete24HourCoverage(last24, now, _options); var et0 = CalculateEt0(last24, _options); var forecast = await GetForecastAsync(cancellationToken);
        var effectiveForecastRain = forecast.RainMm * _options.EffectiveRainFactor * _options.ForecastRainWeight;
        var zones = _options.Zones.Select(zone => { balances.TryGetValue(zone.Id, out var accumulated); var required = Math.Max(0, accumulated - effectiveForecastRain); var shouldRun = required >= _options.IrrigationTriggerMm; var runtime = shouldRun && zone.ApplicationRateMmPerHour > 0 ? (int)Math.Round(required / zone.ApplicationRateMmPerHour * 3600) : 0; return new IrrigationZoneResult(zone.Id, Math.Round(required, 2), Math.Max(0, runtime)); }).ToList();
        var maxDeficit = balances.Count == 0 ? 0 : balances.Values.Max(); var irrigate = zones.Any(x => x.RuntimeSeconds > 0);
        return new IrrigationResult(irrigate, localDataComplete, Math.Round(et0, 2), Math.Round(rain24, 2), Math.Round(rain72, 2), Math.Round(forecast.RainMm, 2), Math.Round(forecast.Et0Mm, 2), Math.Round(maxDeficit, 2), zones, now);
    }

    private void AccumulateCompletedDays(DateTimeOffset nowUtc)
    {
        TimeZoneInfo tz; try { tz = TimeZoneInfo.FindSystemTimeZoneById(_options.TimeZoneId); } catch { tz = TimeZoneInfo.Utc; }
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime); var firstObservation = _state.Observations.Where(x => x.Timestamp is not null).OrderBy(x => x.Timestamp).FirstOrDefault(); if (firstObservation?.Timestamp is null) return;
        var firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(firstObservation.Timestamp.Value, tz).DateTime); var next = _state.LastBalancedLocalDate?.AddDays(1) ?? firstDate;
        while (next < today)
        {
            var dayObs = _state.Observations.Where(x => x.Timestamp is not null && DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Timestamp.Value, tz).DateTime) == next).OrderBy(x => x.Timestamp).ToList();
            if (dayObs.Count >= 2)
            {
                var et0 = CalculateEt0(dayObs, _options); var startUtc = TimeZoneInfo.ConvertTimeToUtc(next.ToDateTime(TimeOnly.MinValue), tz); var endUtc = TimeZoneInfo.ConvertTimeToUtc(next.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);
                var rain = RainDelta(WindowWithBaseline(_state.Observations.OrderBy(x => x.Timestamp).ToList(), new DateTimeOffset(startUtc, TimeSpan.Zero)).Where(x => x.Timestamp < new DateTimeOffset(endUtc, TimeSpan.Zero)).ToList()); var effectiveRain = rain * _options.EffectiveRainFactor;
                foreach (var zone in _options.Zones) { _state.ZoneDeficitMm.TryGetValue(zone.Id, out var current); _state.ZoneDeficitMm[zone.Id] = Math.Max(0, current + et0 * Math.Max(0, zone.Exposure) - effectiveRain); }
            }
            _state.LastBalancedLocalDate = next; next = next.AddDays(1);
        }
    }

    private async Task<ForecastSummary> GetForecastAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow; if (_cachedForecast is not null && now < _forecastRefreshAfter) return _cachedForecast;
        try { _cachedForecast = await _forecast.GetForecastAsync(cancellationToken); _forecastRefreshAfter = now.AddMinutes(30); _forecastValidUntil = now.AddHours(Math.Max(1, _options.ForecastHours)); return _cachedForecast; }
        catch (Exception ex) { Log.Warning(ex, "Unable to retrieve irrigation forecast; continuing without forecast adjustment"); if (_cachedForecast is not null && now < _forecastValidUntil) return _cachedForecast; _cachedForecast = null; return new ForecastSummary(0, 0); }
    }

    private static List<WeatherObservation> WindowWithBaseline(IReadOnlyList<WeatherObservation> observations, DateTimeOffset cutoff) { var window = new List<WeatherObservation>(); var baseline = observations.LastOrDefault(x => x.Timestamp < cutoff); if (baseline is not null) window.Add(baseline); window.AddRange(observations.Where(x => x.Timestamp >= cutoff)); return window; }
    private static bool HasComplete24HourCoverage(IReadOnlyList<WeatherObservation> o, DateTimeOffset now, IrrigationConfiguration options) { if (o.Count < 2) return false; var edgeGap = TimeSpan.FromMinutes(Math.Max(1, options.MaximumObservationEdgeGapMinutes)); return o[0].Timestamp <= now.AddHours(-24) + edgeGap && o[^1].Timestamp >= now - edgeGap; }
    private static double RainDelta(IReadOnlyList<WeatherObservation> o) { if (o.Count < 2) return 0; double total = 0; for (var i = 1; i < o.Count; i++) { var delta = o[i].RainfallMm - o[i - 1].RainfallMm; total += delta >= 0 ? delta : o[i].RainfallMm; } return total; }
    private static double CalculateEt0(IReadOnlyList<WeatherObservation> o, IrrigationConfiguration options) { if (o.Count < 2) return 0; var tMin = o.Min(x => x.TemperatureC); var tMax = o.Max(x => x.TemperatureC); var tMean = o.Average(x => x.TemperatureC); var ea = o.Average(x => SaturationVapourPressure(x.TemperatureC) * x.HumidityPct / 100.0); var es = (SaturationVapourPressure(tMin) + SaturationVapourPressure(tMax)) / 2.0; var vpd = Math.Max(0, es - ea); var pressureKpa = o.Average(x => x.PressureHpa) / 10.0; var gamma = 0.000665 * pressureKpa; var delta = 4098 * SaturationVapourPressure(tMean) / Math.Pow(tMean + 237.3, 2); var wind2m = o.Average(x => x.WindSpeedKmh) / 3.6; var rs = IntegrateSolarMjM2(o, options.LuxPerWattM2); var day = o[^1].Timestamp?.DayOfYear ?? DateTimeOffset.UtcNow.DayOfYear; var ra = ExtraterrestrialRadiation(options.Latitude, day); var rso = (0.75 + 2e-5 * options.ElevationM) * ra; var rns = 0.77 * rs; const double sigma = 4.903e-9; var cloud = rso > 0 ? Math.Clamp(1.35 * Math.Clamp(rs / rso, 0, 1) - 0.35, 0, 1) : 0; var rnl = sigma * (Math.Pow(tMax + 273.16, 4) + Math.Pow(tMin + 273.16, 4)) / 2.0 * (0.34 - 0.14 * Math.Sqrt(Math.Max(0, ea))) * cloud; var rn = Math.Max(0, rns - rnl); var numerator = 0.408 * delta * rn + gamma * (900 / (tMean + 273)) * wind2m * vpd; var denominator = delta + gamma * (1 + 0.34 * wind2m); return denominator > 0 ? Math.Max(0, numerator / denominator) : 0; }
    private static double SaturationVapourPressure(double t) => 0.6108 * Math.Exp(17.27 * t / (t + 237.3));
    private static double IntegrateSolarMjM2(IReadOnlyList<WeatherObservation> o, double luxPerWattM2) { if (luxPerWattM2 <= 0) return 0; double joules = 0; for (var i = 1; i < o.Count; i++) { var seconds = ((o[i].Timestamp ?? DateTimeOffset.MinValue) - (o[i - 1].Timestamp ?? DateTimeOffset.MinValue)).TotalSeconds; if (seconds <= 0 || seconds > 1800) continue; joules += (Math.Max(0, o[i - 1].LightLux / luxPerWattM2) + Math.Max(0, o[i].LightLux / luxPerWattM2)) / 2.0 * seconds; } return joules / 1_000_000.0; }
    private static double ExtraterrestrialRadiation(double latitudeDegrees, int dayOfYear) { var phi = latitudeDegrees * Math.PI / 180.0; var dr = 1 + 0.033 * Math.Cos(2 * Math.PI / 365 * dayOfYear); var sd = 0.409 * Math.Sin(2 * Math.PI / 365 * dayOfYear - 1.39); var ws = Math.Acos(Math.Clamp(-Math.Tan(phi) * Math.Tan(sd), -1, 1)); const double gsc = 0.0820; return 24 * 60 / Math.PI * gsc * dr * (ws * Math.Sin(phi) * Math.Sin(sd) + Math.Cos(phi) * Math.Cos(sd) * Math.Sin(ws)); }
    private IrrigationState LoadState() { try { return File.Exists(_options.StateFile) ? JsonSerializer.Deserialize<IrrigationState>(File.ReadAllText(_options.StateFile)) ?? new() : new(); } catch (Exception ex) { Log.Warning(ex, "Unable to load irrigation state from {file}", _options.StateFile); return new(); } }
    private async Task PersistAsync(CancellationToken cancellationToken) { var directory = Path.GetDirectoryName(_options.StateFile); if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory); var temp = _options.StateFile + ".tmp"; await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_state), cancellationToken); File.Move(temp, _options.StateFile, true); }
}
