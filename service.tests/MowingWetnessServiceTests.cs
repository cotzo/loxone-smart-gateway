using loxone.smart.gateway.Api.Irrigation;
using Xunit;

namespace service.tests;

public sealed class MowingWetnessServiceTests
{
    [Fact]
    public void Drying_IsFasterInSunnyHotDryWindyWeather()
    {
        var options = new IrrigationConfiguration { LuxPerWattM2 = 120 };
        var dry = new WeatherObservation(30, 35, 950, 12, 60000, 0);
        var wet = new WeatherObservation(15, 90, 950, 1, 500, 0);

        var dryRate = MowingWetnessService.EstimateDryingMm(dry, dry, 1, options);
        var wetRate = MowingWetnessService.EstimateDryingMm(wet, wet, 1, options);

        Assert.True(dryRate > wetRate);
        Assert.InRange(dryRate, 0, 0.6);
    }

    [Fact]
    public void SurfaceWetness_AddsRainAndLawnIrrigation()
    {
        var start = DateTimeOffset.Parse("2026-08-21T00:00:00Z");
        var options = new IrrigationConfiguration { MowingDryingFactor = 0 };
        var observations = new[]
        {
            new WeatherObservation(20, 70, 950, 0, 0, 10, start),
            new WeatherObservation(20, 70, 950, 0, 0, 12, start.AddHours(1))
        };
        var runs = new[]
        {
            new IrrigationRun("1", "V6", "LawnSouth", "Irrigation", 600, 3, start.AddMinutes(10), start.AddMinutes(20))
        };

        var wetness = MowingWetnessService.CalculateSurfaceWetnessMm(
            observations, runs, start, start.AddHours(1), options);

        Assert.Equal(5, wetness, 6);
    }

    [Fact]
    public void RainForPeriod_HandlesCounterReset()
    {
        var start = DateTimeOffset.Parse("2026-08-21T00:00:00Z");
        var observations = new[]
        {
            new WeatherObservation(20, 70, 950, 0, 0, 9, start.AddMinutes(-1)),
            new WeatherObservation(20, 70, 950, 0, 0, 10, start.AddMinutes(1)),
            new WeatherObservation(20, 70, 950, 0, 0, 0.5, start.AddMinutes(2)),
            new WeatherObservation(20, 70, 950, 0, 0, 1.5, start.AddMinutes(3))
        };

        Assert.Equal(2.5, MowingWetnessService.RainForPeriod(observations, start, start.AddMinutes(4)), 6);
    }
}
