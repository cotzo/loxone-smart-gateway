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
    public double Latitude { get; set; } = 46.77;
    public double Longitude { get; set; } = 23.59;
    public double ElevationM { get; set; } = 477;
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
    [System.Text.Json.Serialization.JsonPropertyName("mowingAllowedWetnessMm")]
    public double LawnDryWetnessThresholdMm { get; set; } = 1.0;
    public int MowingWetnessLookbackHours { get; set; } = 48;
    public int MowingRainingNowMinutes { get; set; } = 15;
    public double MowingHeavyRainThresholdMm { get; set; } = 10.0;
    public int MowingHeavyRainLockoutHours { get; set; } = 12;
    public int MowingMaximumDryingGapMinutes { get; set; } = 30;
    public int MowingMaximumWeatherAgeMinutes { get; set; } = 10;
    public double MowingDryingFactor { get; set; } = 1.0;

    public List<IrrigationZoneConfiguration> Zones { get; set; } = [];
    public IrrigationLoxoneBlockConfiguration? LoxoneBlock { get; set; }
    public List<IrrigationWeatherStateMapping> WeatherStateMappings { get; set; } = [];
    public IrrigationLoxoneSignalTarget? IrrigationRequiredTarget { get; set; }
    public IrrigationLoxoneSignalTarget? LawnDryTarget { get; set; }
}

public sealed class IrrigationLoxoneSignalTarget
{
    public string ServerId { get; set; } = string.Empty;
    public string StateUuid { get; set; } = string.Empty;
    public string ControlUuid { get; set; } = string.Empty;
    public string Control { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public sealed class IrrigationWeatherStateMapping
{
    public string Field { get; set; } = string.Empty;
    public string ServerId { get; set; } = string.Empty;
    public string StateUuid { get; set; } = string.Empty;
}

public sealed class IrrigationLoxoneBlockConfiguration
{
    public string ServerId { get; set; } = string.Empty;
    public string Control { get; set; } = string.Empty;
    public string ControlUuid { get; set; } = string.Empty;
    public string ActiveStateUuid { get; set; } = string.Empty;
    public string ZonesStateUuid { get; set; } = string.Empty;
    public string CurrentZoneStateUuid { get; set; } = string.Empty;
}

public sealed class IrrigationZoneConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string LoxoneBlockId { get; set; } = string.Empty;
    public string LoxoneZoneId { get; set; } = string.Empty;
    public string LoxoneName { get; set; } = string.Empty;
    public string Type { get; set; } = "Lawn";
    public string BalanceGroup { get; set; } = string.Empty;
    public double Exposure { get; set; } = 1.0;
    // Millimetres of water delivered per hour over the shared root-zone area. For grouped
    // zones this is summed and all member circuits receive the same runtime, so grouping
    // is valid only for parallel circuits that replenish the same physical area. Keep
    // sequential valves or distinct root areas in separate balance groups.
    public double ApplicationRateMmPerHour { get; set; } = 10.0;
}

public sealed record IrrigationZoneResult(
    string Id,
    string BalanceGroup,
    double RequiredMm,
    int RuntimeSeconds,
    double StoredDeficitMm,
    double ForecastRainAdjustmentMm,
    double IrrigationTriggerMm,
    double GroupApplicationRateMmPerHour,
    int CalculatedRuntimeSeconds,
    bool RuntimeCapped,
    bool ShouldIrrigate,
    string DecisionSummary);

public sealed record IrrigationRun(
    string EventId,
    string ZoneId,
    string BalanceGroup,
    string Type,
    int RuntimeSeconds,
    double AppliedMm,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

public sealed record LawnDryStatus(
    bool LawnDry,
    double LawnWetnessMm,
    double AllowedThresholdMm,
    bool WeatherDataFresh,
    bool RainingNow,
    bool LawnIrrigationRunning,
    bool HeavyRainLockout,
    double RainDuringLockoutWindowMm,
    DateTimeOffset CalculatedAt,
    IReadOnlyList<IrrigationDecisionCheck> DecisionChecks);

public sealed record IrrigationDecisionCheck(string Name, bool Passed, string Explanation);

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
    DateTimeOffset CalculatedAt,
    double EffectiveForecastRainMm,
    bool ForecastAvailable,
    string DecisionSummary);

public sealed record IrrigationStoredData(
    IReadOnlyList<WeatherObservation> Observations,
    IReadOnlyDictionary<string, double> BalanceDeficitMm,
    DateOnly? LastBalancedLocalDate,
    IReadOnlyList<IrrigationRun> IrrigationRuns,
    int TotalObservationCount);

public sealed record IrrigationDashboardSnapshot(
    IrrigationConfiguration Configuration,
    IrrigationStoredData Stored,
    IReadOnlyList<string> ActiveZoneIds,
    IrrigationResult Decision,
    LawnDryStatus LawnDry);

internal sealed class IrrigationState
{
    public List<WeatherObservation> Observations { get; set; } = [];
    public Dictionary<string, double> BalanceDeficitMm { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateOnly? LastBalancedLocalDate { get; set; }
    public List<IrrigationRun> IrrigationRuns { get; set; } = [];
}
