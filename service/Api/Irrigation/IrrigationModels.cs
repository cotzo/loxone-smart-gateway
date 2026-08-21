namespace loxone.smart.gateway.Api.Irrigation;

public sealed record WeatherObservation(
    double TemperatureC,
    double HumidityPct,
    double PressureHpa,
    double WindSpeedKmh,
    double LightLux,
    double RainfallMm,
    DateTimeOffset? Timestamp = null);

public sealed class IrrigationConfiguration
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double ElevationM { get; set; }
    public double LuxPerWattM2 { get; set; } = 120;
    public double EffectiveRainFactor { get; set; } = 0.8;
    public double ForecastRainWeight { get; set; } = 0.75;
    public double IrrigationTriggerMm { get; set; } = 5.0;
    public double MaximumDeficitMm { get; set; } = 15.0;
    public int MaximumZoneRuntimeSeconds { get; set; } = 1800;
    public int ForecastHours { get; set; } = 24;
    public int MaximumTimestampSkewMinutes { get; set; } = 5;
    public int MaximumObservationEdgeGapMinutes { get; set; } = 15;
    public int CompletedDayEdgeGapMinutes { get; set; } = 30;
    public string TimeZoneId { get; set; } = "Europe/Bucharest";
    public string StateFile { get; set; } = "data/irrigation-state.json";

    // Surface wetness / mower protection. This is intentionally separate from the root-zone
    // irrigation deficit: the mower cares about traction and soft surface soil on slopes.
    public double MowingAllowedWetnessMm { get; set; } = 1.0;
    public int MowingWetnessLookbackHours { get; set; } = 48;
    public int MowingRainingNowMinutes { get; set; } = 15;
    public double MowingHeavyRainThresholdMm { get; set; } = 10.0;
    public int MowingHeavyRainLockoutHours { get; set; } = 12;
    public int MowingMaximumDryingGapMinutes { get; set; } = 30;
    public double MowingDryingFactor { get; set; } = 1.0;

    public List<IrrigationZoneConfiguration> Zones { get; set; } = [];
}

public sealed class IrrigationZoneConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "Lawn";
    public string BalanceGroup { get; set; } = string.Empty;
    public double Exposure { get; set; } = 1.0;
    // Physical water delivered by this single valve/circuit.
    public double ApplicationRateMmPerHour { get; set; } = 10.0;
}

public sealed record IrrigationZoneResult(string Id, string BalanceGroup, double RequiredMm, int RuntimeSeconds);

public sealed record IrrigationRun(
    string EventId,
    string ZoneId,
    string BalanceGroup,
    string Type,
    int RuntimeSeconds,
    double AppliedMm,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

public sealed record MowingStatus(
    bool MowingAllowed,
    double LawnWetnessMm,
    double AllowedThresholdMm,
    bool RainingNow,
    bool LawnIrrigationRunning,
    bool HeavyRainLockout,
    double RainDuringLockoutWindowMm,
    DateTimeOffset CalculatedAt);

public sealed record IrrigationResult(
    bool Irrigate,
    bool LocalDataComplete,
    double Et0Observed24hMm,
    double Rain24hMm,
    double Rain72hMm,
    double ForecastRainMm,
    double ForecastEt0Mm,
    double WaterDeficitMm,
    IReadOnlyList<IrrigationZoneResult> Zones,
    DateTimeOffset CalculatedAt);

internal sealed class IrrigationState
{
    public List<WeatherObservation> Observations { get; set; } = [];
    public Dictionary<string, double> BalanceDeficitMm { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateOnly? LastBalancedLocalDate { get; set; }
    public List<IrrigationRun> IrrigationRuns { get; set; } = [];
}
