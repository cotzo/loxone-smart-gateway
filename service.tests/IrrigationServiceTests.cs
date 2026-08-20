using System.Net;
using System.Text;
using System.Text.Json;
using loxone.smart.gateway.Api.Irrigation;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace service.tests;

public sealed class IrrigationServiceTests
{
    [Fact]
    public void RainDelta_HandlesCounterReset()
    {
        var t = DateTimeOffset.Parse("2026-08-20T00:00:00Z");
        var observations = new[]
        {
            Obs(10, t),
            Obs(12, t.AddMinutes(1)),
            Obs(0.5, t.AddMinutes(2)),
            Obs(1.5, t.AddMinutes(3))
        };

        Assert.Equal(3.5, IrrigationService.RainDelta(observations), 6);
    }

    [Fact]
    public void RainForPeriod_UsesBaselineBeforeBoundary()
    {
        var start = DateTimeOffset.Parse("2026-08-20T00:00:00Z");
        var end = start.AddDays(1);
        var observations = new[]
        {
            Obs(4, start.AddMinutes(-1)),
            Obs(5, start.AddMinutes(1)),
            Obs(7, start.AddHours(12)),
            Obs(8, end.AddMinutes(1))
        };

        Assert.Equal(3, IrrigationService.RainForPeriod(observations, start, end), 6);
    }

    [Fact]
    public void CompleteDayCoverage_RejectsPartialDay()
    {
        var start = DateTimeOffset.Parse("2026-08-20T00:00:00Z");
        var end = start.AddDays(1);
        var gap = TimeSpan.FromMinutes(30);

        Assert.True(IrrigationService.HasCompleteDayCoverage(
            new[] { Obs(0, start.AddMinutes(5)), Obs(0, end.AddMinutes(-5)) }, start, end, gap));

        Assert.False(IrrigationService.HasCompleteDayCoverage(
            new[] { Obs(0, start.AddHours(12)), Obs(0, end.AddMinutes(-5)) }, start, end, gap));
    }

    [Fact]
    public void BalanceGroup_FallsBackToZoneId()
    {
        Assert.Equal("V1", IrrigationService.ResolveBalanceGroup(new IrrigationZoneConfiguration { Id = "V1" }));
        Assert.Equal("LawnEast", IrrigationService.ResolveBalanceGroup(new IrrigationZoneConfiguration { Id = "V1", BalanceGroup = " LawnEast " }));
    }

    [Fact]
    public async Task AppliedEvent_IsIdempotent_AndSharedGroupUsesPhysicalValveRate()
    {
        var stateFile = TempState("{\"Observations\":[],\"BalanceDeficitMm\":{\"LawnEast\":10},\"IrrigationRuns\":[]}");
        try
        {
            var service = CreateService(stateFile, new Dictionary<string, string?>
            {
                ["Api:IrrigationConfiguration:Zones:0:Id"] = "V1",
                ["Api:IrrigationConfiguration:Zones:0:BalanceGroup"] = "LawnEast",
                ["Api:IrrigationConfiguration:Zones:0:Exposure"] = "0.65",
                ["Api:IrrigationConfiguration:Zones:0:ApplicationRateMmPerHour"] = "12"
            });

            Assert.True(await service.RecordIrrigationAsync("V1", 600, "run-1", "Irrigation", CancellationToken.None));
            Assert.True(await service.RecordIrrigationAsync("V1", 600, "run-1", "Irrigation", CancellationToken.None));

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile));
            Assert.Equal(8, doc.RootElement.GetProperty("BalanceDeficitMm").GetProperty("LawnEast").GetDouble(), 6);
            Assert.Single(doc.RootElement.GetProperty("IrrigationRuns").EnumerateArray());
        }
        finally
        {
            File.Delete(stateFile);
        }
    }

    [Fact]
    public async Task Rinse_IsRecordedButDoesNotChangeBalance()
    {
        var stateFile = TempState("{\"Observations\":[],\"BalanceDeficitMm\":{\"V6\":5},\"IrrigationRuns\":[]}");
        try
        {
            var service = CreateService(stateFile, new Dictionary<string, string?>
            {
                ["Api:IrrigationConfiguration:Zones:0:Id"] = "V6",
                ["Api:IrrigationConfiguration:Zones:0:ApplicationRateMmPerHour"] = "25.4"
            });

            Assert.True(await service.RecordIrrigationAsync("V6", 120, "rinse-1", "Rinse", CancellationToken.None));
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile));
            Assert.Equal(5, doc.RootElement.GetProperty("BalanceDeficitMm").GetProperty("V6").GetDouble(), 6);
            Assert.Equal("Rinse", doc.RootElement.GetProperty("IrrigationRuns")[0].GetProperty("Type").GetString());
        }
        finally
        {
            File.Delete(stateFile);
        }
    }

    [Fact]
    public async Task SharedBalanceGroup_RuntimeUsesCombinedApplicationRate()
    {
        var stateFile = TempState("{\"Observations\":[],\"BalanceDeficitMm\":{\"LawnEast\":5},\"IrrigationRuns\":[]}");
        try
        {
            var service = CreateService(stateFile, new Dictionary<string, string?>
            {
                ["Api:IrrigationConfiguration:IrrigationTriggerMm"] = "5",
                ["Api:IrrigationConfiguration:MaximumZoneRuntimeSeconds"] = "1800",
                ["Api:IrrigationConfiguration:ForecastHours"] = "1",
                ["Api:IrrigationConfiguration:Zones:0:Id"] = "V1",
                ["Api:IrrigationConfiguration:Zones:0:BalanceGroup"] = "LawnEast",
                ["Api:IrrigationConfiguration:Zones:0:Exposure"] = "0.65",
                ["Api:IrrigationConfiguration:Zones:0:ApplicationRateMmPerHour"] = "11.65",
                ["Api:IrrigationConfiguration:Zones:1:Id"] = "V4",
                ["Api:IrrigationConfiguration:Zones:1:BalanceGroup"] = "LawnEast",
                ["Api:IrrigationConfiguration:Zones:1:Exposure"] = "0.65",
                ["Api:IrrigationConfiguration:Zones:1:ApplicationRateMmPerHour"] = "11.65"
            });

            var result = await service.CalculateAsync(CancellationToken.None);
            var v1 = result.Zones.Single(x => x.Id == "V1");
            var v4 = result.Zones.Single(x => x.Id == "V4");
            Assert.Equal(773, v1.RuntimeSeconds);
            Assert.Equal(v1.RuntimeSeconds, v4.RuntimeSeconds);
        }
        finally
        {
            File.Delete(stateFile);
        }
    }

    [Fact]
    public async Task Runtime_IsCapped()
    {
        var stateFile = TempState("{\"Observations\":[],\"BalanceDeficitMm\":{\"V6\":15},\"IrrigationRuns\":[]}");
        try
        {
            var service = CreateService(stateFile, new Dictionary<string, string?>
            {
                ["Api:IrrigationConfiguration:IrrigationTriggerMm"] = "5",
                ["Api:IrrigationConfiguration:MaximumZoneRuntimeSeconds"] = "900",
                ["Api:IrrigationConfiguration:ForecastHours"] = "1",
                ["Api:IrrigationConfiguration:Zones:0:Id"] = "V6",
                ["Api:IrrigationConfiguration:Zones:0:ApplicationRateMmPerHour"] = "25.4"
            });

            var result = await service.CalculateAsync(CancellationToken.None);
            Assert.Equal(900, result.Zones.Single().RuntimeSeconds);
        }
        finally
        {
            File.Delete(stateFile);
        }
    }

    private static WeatherObservation Obs(double rain, DateTimeOffset timestamp) =>
        new(20, 50, 1000, 5, 10000, rain, timestamp);

    private static string TempState(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"irrigation-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static IrrigationService CreateService(string stateFile, IDictionary<string, string?> extra)
    {
        var values = new Dictionary<string, string?>(extra)
        {
            ["Api:IrrigationConfiguration:StateFile"] = stateFile,
            ["Api:IrrigationConfiguration:Latitude"] = "46.77",
            ["Api:IrrigationConfiguration:Longitude"] = "23.59",
            ["Api:IrrigationConfiguration:ElevationM"] = "477",
            ["Api:IrrigationConfiguration:ForecastRainWeight"] = "0.75"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var http = new HttpClient(new ForecastHandler()) { BaseAddress = new Uri("https://example.test/") };
        return new IrrigationService(configuration, new OpenMeteoForecastClient(http, configuration));
    }

    private sealed class ForecastHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"hourly\":{\"precipitation\":[0],\"et0_fao_evapotranspiration\":[0]}}",
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
