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
            {
                Log.Warning(
                    "Ignoring weather observation timestamp {timestamp}; it differs from gateway time {receivedAt} by more than {allowedSkew}",
                    timestamp, receivedAt, allowedSkew);
                timestamp = receivedAt;
            }

            var normalized = observation with
            {
                Timestamp = timestamp,
                HumidityPct = Math.Clamp(observation.HumidityPct, 0, 100),
                RainfallMm = Math.Max(0, observation.RainfallMm)
            };

            _state.Observations.Add(normalized);

            var retentionCutoff = receivedAt.AddDays(-4);
            _state.Observations.RemoveAll(x => (x.Timestamp ?? DateTimeOffset.MinValue) < retentionCutoff);
            await PersistAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IrrigationResult> CalculateAsync(CancellationToken cancellationToken)
    {
        List<WeatherObservation> observations;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            observations = [.. _state.Observations];
        }
        finally
        {
            _gate.Release();
        }

        var now = DateTimeOffset.UtcNow;
        observations = observations
            .Where(x => x.Timestamp is not null && x.Timestamp <= now.AddMinutes(Math.Max(0, _options.MaximumTimestampSkewMinutes)))
            .OrderBy(x => x.Timestamp)
            .ToList();

        var cutoff24 = now.AddHours(-24);
        var cutoff72 = now.AddHours(-72);
        var last24 = observations.Where(x => x.Timestamp >= cutoff24).ToList();
        var rain24Window = WindowWithBaseline(observations, cutoff24);
        var rain72Window = WindowWithBaseline(observations, cutoff72);

        var rain24 = RainDelta(rain24Window);
        var rain72 = RainDelta(rain72Window);
        var localDataComplete = HasComplete24HourCoverage(last24, now, _options);
        // Calculate with whatever local history is currently available. LocalDataComplete remains
        // diagnostic only until the rolling 24-hour window is populated.
        var et0 = CalculateEt0(last24, _options);
        var forecast = await GetForecastAsync(cancellationToken);

        var effectiveMeasuredRain = rain24 * _options.EffectiveRainFactor;
        var effectiveForecastRain = forecast.RainMm * _options.EffectiveRainFactor * _options.ForecastRainWeight;
        var deficit = Math.Max(0, et0 - effectiveMeasuredRain - effectiveForecastRain);
        var irrigate = deficit >= _options.MinimumIrrigationMm;

        var zones = _options.Zones.Select(zone =>
        {
            var required = irrigate ? deficit * Math.Max(0, zone.Exposure) : 0;
            var runtime = zone.ApplicationRateMmPerHour <= 0
                ? 0
                : (int)Math.Round(required / zone.ApplicationRateMmPerHour * 3600);
            return new IrrigationZoneResult(zone.Id, Math.Round(required, 2), Math.Max(0, runtime));
        }).ToList();

        return new IrrigationResult(
            irrigate,
            localDataComplete,
            Math.Round(et0, 2),
            Math.Round(rain24, 2),
            Math.Round(rain72, 2),
            Math.Round(forecast.RainMm, 2),
            Math.Round(forecast.Et0Mm, 2),
            Math.Round(deficit, 2),
            zones,
            now);
    }

    private async Task<ForecastSummary> GetForecastAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedForecast is not null && now < _forecastRefreshAfter)
            return _cachedForecast;

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
            if (_cachedForecast is not null && now < _forecastValidUntil)
                return _cachedForecast;

            _cachedForecast = null;
            _forecastRefreshAfter = DateTimeOffset.MinValue;
            _forecastValidUntil = DateTimeOffset.MinValue;
            return new ForecastSummary(0, 0);
        }
    }

    private static List<WeatherObservation> WindowWithBaseline(IReadOnlyList<WeatherObservation> observations, DateTimeOffset cutoff)
    {
        var window = new List<WeatherObservation>();
        var baseline = observations.LastOrDefault(x => x.Timestamp < cutoff);
        if (baseline is not null) window.Add(baseline);
        window.AddRange(observations.Where(x => x.Timestamp >= cutoff));
        return window;
    }

    private static bool HasComplete24HourCoverage(IReadOnlyList<WeatherObservation> observations, DateTimeOffset now, IrrigationConfiguration options)
    {
        if (observations.Count < 2) return false;
        var edgeGap = TimeSpan.FromMinutes(Math.Max(1, options.MaximumObservationEdgeGapMinutes));
        var cutoff = now.AddHours(-24);
        var first = observations[0].Timestamp ?? DateTimeOffset.MaxValue;
        var last = observations[^1].Timestamp ?? DateTimeOffset.MinValue;
        return first <= cutoff + edgeGap && last >= now - edgeGap;
    }

    private static double RainDelta(IReadOnlyList<WeatherObservation> observations)
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

    private static double CalculateEt0(IReadOnlyList<WeatherObservation> o, IrrigationConfiguration options)
    {
        if (o.Count < 2) return 0;
        var tMin = o.Min(x => x.TemperatureC);
        var tMax = o.Max(x => x.TemperatureC);
        var tMean = o.Average(x => x.TemperatureC);
        var ea = o.Average(x => SaturationVapourPressure(x.TemperatureC) * x.HumidityPct / 100.0);
        var es = (SaturationVapourPressure(tMin) + SaturationVapourPressure(tMax)) / 2.0;
        var vpd = Math.Max(0, es - ea);
        var pressureKpa = o.Average(x => x.PressureHpa) / 10.0;
        var gamma = 0.000665 * pressureKpa;
        var delta = 4098 * SaturationVapourPressure(tMean) / Math.Pow(tMean + 237.3, 2);
        var wind2m = o.Average(x => x.WindSpeedKmh) / 3.6;
        var rs = IntegrateSolarMjM2(o, options.LuxPerWattM2);
        var day = o[^1].Timestamp?.DayOfYear ?? DateTimeOffset.UtcNow.DayOfYear;
        var ra = ExtraterrestrialRadiation(options.Latitude, day);
        var rso = (0.75 + 2e-5 * options.ElevationM) * ra;
        var rns = 0.77 * rs;
        var sigma = 4.903e-9;
        var tMaxK = tMax + 273.16;
        var tMinK = tMin + 273.16;
        var cloud = rso > 0 ? Math.Clamp(1.35 * Math.Clamp(rs / rso, 0, 1) - 0.35, 0, 1) : 0;
        var rnl = sigma * (Math.Pow(tMaxK, 4) + Math.Pow(tMinK, 4)) / 2.0 *
                  (0.34 - 0.14 * Math.Sqrt(Math.Max(0, ea))) * cloud;
        var rn = Math.Max(0, rns - rnl);
        var numerator = 0.408 * delta * rn + gamma * (900 / (tMean + 273)) * wind2m * vpd;
        var denominator = delta + gamma * (1 + 0.34 * wind2m);
        return denominator > 0 ? Math.Max(0, numerator / denominator) : 0;
    }

    private static double SaturationVapourPressure(double t) => 0.6108 * Math.Exp(17.27 * t / (t + 237.3));

    private static double IntegrateSolarMjM2(IReadOnlyList<WeatherObservation> o, double luxPerWattM2)
    {
        if (luxPerWattM2 <= 0) return 0;
        double joules = 0;
        for (var i = 1; i < o.Count; i++)
        {
            var seconds = ((o[i].Timestamp ?? DateTimeOffset.MinValue) - (o[i - 1].Timestamp ?? DateTimeOffset.MinValue)).TotalSeconds;
            if (seconds <= 0 || seconds > 1800) continue;
            var w1 = Math.Max(0, o[i - 1].LightLux / luxPerWattM2);
            var w2 = Math.Max(0, o[i].LightLux / luxPerWattM2);
            joules += (w1 + w2) / 2.0 * seconds;
        }
        return joules / 1_000_000.0;
    }

    private static double ExtraterrestrialRadiation(double latitudeDegrees, int dayOfYear)
    {
        var phi = latitudeDegrees * Math.PI / 180.0;
        var dr = 1 + 0.033 * Math.Cos(2 * Math.PI / 365 * dayOfYear);
        var solarDeclination = 0.409 * Math.Sin(2 * Math.PI / 365 * dayOfYear - 1.39);
        var ws = Math.Acos(Math.Clamp(-Math.Tan(phi) * Math.Tan(solarDeclination), -1, 1));
        const double gsc = 0.0820;
        return 24 * 60 / Math.PI * gsc * dr *
               (ws * Math.Sin(phi) * Math.Sin(solarDeclination) + Math.Cos(phi) * Math.Cos(solarDeclination) * Math.Sin(ws));
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
