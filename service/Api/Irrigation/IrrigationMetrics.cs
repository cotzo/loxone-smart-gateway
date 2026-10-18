using System.Diagnostics.Metrics;

namespace loxone.smart.gateway.Api.Irrigation;

/// <summary>Prometheus instruments for persisted weather history and irrigation decisions.</summary>
public sealed class IrrigationMetrics : IDisposable
{
    public const string MeterName = "LoxoneSmartGateway.Irrigation";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _observations;
    private readonly Counter<long> _runs;
    private readonly Histogram<double> _observationInterval;
    private readonly Histogram<double> _runDuration;
    private readonly Histogram<double> _waterApplied;
    private readonly Histogram<double> _calculationDuration;
    private long _observationCount;
    private long _runCount;
    private long _balanceGroupCount;
    private long _latestObservationTicks;
    private long _historySpanTicks;
    private long _irrigationRequired;
    private long _weatherComplete;
    private long _forecastAvailable;
    private long _lastCalculationTicks;
    private long _lawnDry;
    private long _lawnWetnessMicromm;
    private long _lawnWeatherFresh;
    private long _lawnRaining;
    private long _lawnIrrigationRunning;
    private long _lawnHeavyRainLockout;
    private long _temperatureMilliC;
    private long _humidityMilliPct;
    private long _pressureMilliHpa;
    private long _windMilliKmh;
    private long _lightLux;
    private long _rainCounterMilliMm;
    private long _rain24MilliMm;
    private long _rain72MilliMm;
    private long _observedEt0MilliMm;
    private long _forecastRainMilliMm;
    private long _forecastEt0MilliMm;
    private long _waterDeficitMilliMm;

    public IrrigationMetrics()
    {
        _observations = _meter.CreateCounter<long>("irrigation.weather.observations.committed", unit: "{observation}", description: "Mapped weather observations persisted.");
        _runs = _meter.CreateCounter<long>("irrigation.runs.completed", unit: "{run}", description: "Irrigation runs recorded after a zone stops.");
        _observationInterval = _meter.CreateHistogram<double>("irrigation.weather.observation.interval", unit: "s", description: "Elapsed time between committed weather observations.");
        _runDuration = _meter.CreateHistogram<double>("irrigation.run.duration", unit: "s", description: "Recorded irrigation zone runtime.");
        _waterApplied = _meter.CreateHistogram<double>("irrigation.run.water.applied", unit: "mm", description: "Estimated water applied by completed irrigation runs.");
        _calculationDuration = _meter.CreateHistogram<double>("irrigation.decision.calculation.duration", unit: "ms", description: "Irrigation recommendation calculation duration.");
        _meter.CreateObservableGauge("irrigation.weather.observations.stored", () => Interlocked.Read(ref _observationCount), unit: "{observation}", description: "Number of weather observations currently retained.");
        _meter.CreateObservableGauge("irrigation.weather.latest.age", () => AgeSeconds(Interlocked.Read(ref _latestObservationTicks)), unit: "s", description: "Age of the newest stored weather observation.");
        _meter.CreateObservableGauge("irrigation.weather.history.span", () => Interlocked.Read(ref _historySpanTicks) / (double)TimeSpan.TicksPerSecond, unit: "s", description: "Time covered by retained weather observations.");
        _meter.CreateObservableGauge("irrigation.weather.observed.temperature", () => Interlocked.Read(ref _temperatureMilliC) / 1000d, unit: "Cel", description: "Temperature from the latest persisted weather observation.");
        _meter.CreateObservableGauge("irrigation.weather.observed.humidity", () => Interlocked.Read(ref _humidityMilliPct) / 1000d, unit: "%", description: "Relative humidity from the latest persisted weather observation.");
        _meter.CreateObservableGauge("irrigation.weather.observed.pressure", () => Interlocked.Read(ref _pressureMilliHpa) / 1000d, unit: "hPa", description: "Pressure from the latest persisted weather observation.");
        _meter.CreateObservableGauge("irrigation.weather.observed.wind_speed", () => Interlocked.Read(ref _windMilliKmh) / 1000d, unit: "km/h", description: "Wind speed from the latest persisted weather observation.");
        _meter.CreateObservableGauge("irrigation.weather.observed.light", () => Interlocked.Read(ref _lightLux), unit: "lx", description: "Light level from the latest persisted weather observation.");
        _meter.CreateObservableGauge("irrigation.weather.observed.rainfall_counter", () => Interlocked.Read(ref _rainCounterMilliMm) / 1000d, unit: "mm", description: "Cumulative rainfall counter from the latest persisted weather observation.");
        _meter.CreateObservableGauge("irrigation.runs.stored", () => Interlocked.Read(ref _runCount), unit: "{run}", description: "Number of irrigation runs currently retained.");
        _meter.CreateObservableGauge("irrigation.balance.groups", () => Interlocked.Read(ref _balanceGroupCount), unit: "{group}", description: "Configured water-balance groups.");
        _meter.CreateObservableGauge("irrigation.decision.required", () => Interlocked.Read(ref _irrigationRequired), unit: "{boolean}", description: "Whether the latest irrigation calculation recommends any watering.");
        _meter.CreateObservableGauge("irrigation.decision.weather.complete", () => Interlocked.Read(ref _weatherComplete), unit: "{boolean}", description: "Whether the latest irrigation calculation has complete local weather history.");
        _meter.CreateObservableGauge("irrigation.decision.forecast.available", () => Interlocked.Read(ref _forecastAvailable), unit: "{boolean}", description: "Whether a usable forecast was available to the latest irrigation calculation.");
        _meter.CreateObservableGauge("irrigation.decision.rain.observed.24h", () => Interlocked.Read(ref _rain24MilliMm) / 1000d, unit: "mm", description: "Observed rainfall over the last 24 hours at the latest calculation.");
        _meter.CreateObservableGauge("irrigation.decision.rain.observed.72h", () => Interlocked.Read(ref _rain72MilliMm) / 1000d, unit: "mm", description: "Observed rainfall over the last 72 hours at the latest calculation.");
        _meter.CreateObservableGauge("irrigation.decision.et0.observed.24h", () => Interlocked.Read(ref _observedEt0MilliMm) / 1000d, unit: "mm", description: "Estimated observed ET0 over the last 24 hours at the latest calculation.");
        _meter.CreateObservableGauge("irrigation.decision.rain.forecast", () => Interlocked.Read(ref _forecastRainMilliMm) / 1000d, unit: "mm", description: "Forecast rainfall used by the latest calculation.");
        _meter.CreateObservableGauge("irrigation.decision.et0.forecast", () => Interlocked.Read(ref _forecastEt0MilliMm) / 1000d, unit: "mm", description: "Forecast ET0 used by the latest calculation.");
        _meter.CreateObservableGauge("irrigation.decision.water.deficit", () => Interlocked.Read(ref _waterDeficitMilliMm) / 1000d, unit: "mm", description: "Maximum stored water deficit at the latest irrigation calculation.");
        _meter.CreateObservableGauge("irrigation.decision.latest.age", () => AgeSeconds(Interlocked.Read(ref _lastCalculationTicks)), unit: "s", description: "Age of the latest irrigation recommendation calculation.");
        _meter.CreateObservableGauge("irrigation.lawn_dry.allowed", () => Interlocked.Read(ref _lawnDry), unit: "{boolean}", description: "Whether the latest Lawn Dry calculation allows mowing.");
        _meter.CreateObservableGauge("irrigation.lawn_dry.surface_wetness", () => Interlocked.Read(ref _lawnWetnessMicromm) / 1_000_000d, unit: "mm", description: "Estimated surface wetness in the latest Lawn Dry calculation.");
        _meter.CreateObservableGauge("irrigation.lawn_dry.weather_fresh", () => Interlocked.Read(ref _lawnWeatherFresh), unit: "{boolean}", description: "Whether latest Lawn Dry calculation has fresh sufficient weather history.");
        _meter.CreateObservableGauge("irrigation.lawn_dry.raining", () => Interlocked.Read(ref _lawnRaining), unit: "{boolean}", description: "Whether rain was detected in the latest Lawn Dry calculation.");
        _meter.CreateObservableGauge("irrigation.lawn_dry.irrigation_running", () => Interlocked.Read(ref _lawnIrrigationRunning), unit: "{boolean}", description: "Whether a configured lawn irrigation zone is active.");
        _meter.CreateObservableGauge("irrigation.lawn_dry.heavy_rain_lockout", () => Interlocked.Read(ref _lawnHeavyRainLockout), unit: "{boolean}", description: "Whether the latest Lawn Dry calculation is blocked by heavy rain.");
    }

    public void RecordObservation(DateTimeOffset timestamp, DateTimeOffset? previousTimestamp, int retainedCount)
    {
        _observations.Add(1);
        if (previousTimestamp is not null)
            _observationInterval.Record(Math.Max(0, (timestamp - previousTimestamp.Value).TotalSeconds));
        Interlocked.Exchange(ref _latestObservationTicks, timestamp.UtcTicks);
        Interlocked.Exchange(ref _observationCount, retainedCount);
    }

    public void UpdateStoredData(IrrigationStoredData data)
    {
        Interlocked.Exchange(ref _observationCount, data.TotalObservationCount);
        Interlocked.Exchange(ref _runCount, data.IrrigationRuns.Count);
        Interlocked.Exchange(ref _balanceGroupCount, data.BalanceDeficitMm.Count);
        var observations = data.Observations.Where(x => x.Timestamp is not null).Select(x => x.Timestamp!.Value).Order().ToArray();
        Interlocked.Exchange(ref _latestObservationTicks, observations.LastOrDefault().UtcTicks);
        Interlocked.Exchange(ref _historySpanTicks, observations.Length < 2 ? 0 : (observations[^1] - observations[0]).Ticks);
        var latest = data.Observations.Where(x => x.Timestamp is not null).MaxBy(x => x.Timestamp);
        if (latest is not null)
        {
            Interlocked.Exchange(ref _temperatureMilliC, Scale(latest.TemperatureC));
            Interlocked.Exchange(ref _humidityMilliPct, Scale(latest.HumidityPct));
            Interlocked.Exchange(ref _pressureMilliHpa, Scale(latest.PressureHpa));
            Interlocked.Exchange(ref _windMilliKmh, Scale(latest.WindSpeedKmh));
            Interlocked.Exchange(ref _lightLux, Scale(latest.LightLux));
            Interlocked.Exchange(ref _rainCounterMilliMm, Scale(latest.RainfallMm));
        }
    }

    public void RecordRun(int runtimeSeconds, double appliedMm)
    {
        _runs.Add(1);
        Interlocked.Increment(ref _runCount);
        _runDuration.Record(Math.Max(0, runtimeSeconds));
        _waterApplied.Record(Math.Max(0, appliedMm));
    }

    public void RecordDecision(IrrigationResult result, double durationMs)
    {
        Interlocked.Exchange(ref _irrigationRequired, result.Irrigate ? 1 : 0);
        Interlocked.Exchange(ref _weatherComplete, result.LocalDataComplete ? 1 : 0);
        Interlocked.Exchange(ref _forecastAvailable, result.ForecastAvailable ? 1 : 0);
        Interlocked.Exchange(ref _rain24MilliMm, Scale(result.Rain24hMm));
        Interlocked.Exchange(ref _rain72MilliMm, Scale(result.Rain72hMm));
        Interlocked.Exchange(ref _observedEt0MilliMm, Scale(result.Et0Observed24hMm));
        Interlocked.Exchange(ref _forecastRainMilliMm, Scale(result.ForecastRainMm));
        Interlocked.Exchange(ref _forecastEt0MilliMm, Scale(result.ForecastEt0Mm));
        Interlocked.Exchange(ref _waterDeficitMilliMm, Scale(result.WaterDeficitMm));
        Interlocked.Exchange(ref _lastCalculationTicks, DateTimeOffset.UtcNow.UtcTicks);
        _calculationDuration.Record(Math.Max(0, durationMs));
    }

    public void RecordLawnDryDecision(LawnDryStatus status)
    {
        Interlocked.Exchange(ref _lawnDry, status.LawnDry ? 1 : 0);
        Interlocked.Exchange(ref _lawnWetnessMicromm, (long)Math.Round(status.LawnWetnessMm * 1_000_000));
        Interlocked.Exchange(ref _lawnWeatherFresh, status.WeatherDataFresh ? 1 : 0);
        Interlocked.Exchange(ref _lawnRaining, status.RainingNow ? 1 : 0);
        Interlocked.Exchange(ref _lawnIrrigationRunning, status.LawnIrrigationRunning ? 1 : 0);
        Interlocked.Exchange(ref _lawnHeavyRainLockout, status.HeavyRainLockout ? 1 : 0);
    }

    private static double AgeSeconds(long utcTicks) => utcTicks <= 0
        ? 0
        : Math.Max(0, (DateTimeOffset.UtcNow.UtcTicks - utcTicks) / (double)TimeSpan.TicksPerSecond);

    private static long Scale(double value) => double.IsFinite(value) ? (long)Math.Clamp(Math.Round(value * 1000), long.MinValue, long.MaxValue) : 0;

    public void Dispose() => _meter.Dispose();
}
