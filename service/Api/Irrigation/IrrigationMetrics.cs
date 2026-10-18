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
        _meter.CreateObservableGauge("irrigation.runs.stored", () => Interlocked.Read(ref _runCount), unit: "{run}", description: "Number of irrigation runs currently retained.");
        _meter.CreateObservableGauge("irrigation.balance.groups", () => Interlocked.Read(ref _balanceGroupCount), unit: "{group}", description: "Configured water-balance groups.");
        _meter.CreateObservableGauge("irrigation.decision.required", () => Interlocked.Read(ref _irrigationRequired), unit: "{boolean}", description: "Whether the latest irrigation calculation recommends any watering.");
        _meter.CreateObservableGauge("irrigation.decision.weather.complete", () => Interlocked.Read(ref _weatherComplete), unit: "{boolean}", description: "Whether the latest irrigation calculation has complete local weather history.");
        _meter.CreateObservableGauge("irrigation.decision.forecast.available", () => Interlocked.Read(ref _forecastAvailable), unit: "{boolean}", description: "Whether a usable forecast was available to the latest irrigation calculation.");
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
    }

    public void RecordRun(int runtimeSeconds, double appliedMm)
    {
        _runs.Add(1);
        Interlocked.Increment(ref _runCount);
        _runDuration.Record(Math.Max(0, runtimeSeconds));
        _waterApplied.Record(Math.Max(0, appliedMm));
    }

    public void RecordDecision(bool irrigationRequired, bool weatherComplete, bool forecastAvailable, double durationMs)
    {
        Interlocked.Exchange(ref _irrigationRequired, irrigationRequired ? 1 : 0);
        Interlocked.Exchange(ref _weatherComplete, weatherComplete ? 1 : 0);
        Interlocked.Exchange(ref _forecastAvailable, forecastAvailable ? 1 : 0);
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
        ? -1
        : Math.Max(0, (DateTimeOffset.UtcNow.UtcTicks - utcTicks) / (double)TimeSpan.TicksPerSecond);

    public void Dispose() => _meter.Dispose();
}
