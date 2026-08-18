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
    public double ForecastRainWeight { get; set; } = 1.0;
    public double MinimumIrrigationMm { get; set; } = 2.0;
    public int ForecastHours { get; set; } = 24;
    public string StateFile { get; set; } = "data/irrigation-state.json";
    public List<IrrigationZoneConfiguration> Zones { get; set; } = [];
}

public sealed class IrrigationZoneConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "Lawn";
    public double Exposure { get; set; } = 1.0;
    // Water delivered by this zone. Measure this in the field for accurate runtimes.
    public double ApplicationRateMmPerHour { get; set; } = 10.0;
}

public sealed record IrrigationZoneResult(string Id, double RequiredMm, int RuntimeSeconds);

public sealed record IrrigationResult(
    bool Irrigate,
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
}
