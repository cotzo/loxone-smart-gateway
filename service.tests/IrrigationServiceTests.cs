using System.Net;
using System.Text;
using System.Text.Json;
using loxone.smart.gateway.Api.Gateway;
using loxone.smart.gateway.Api.Irrigation;
using Microsoft.AspNetCore.DataProtection;
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
    public void SeasonalCoefficient_UsesBalanceGroupMonthAndNeutralFallback()
    {
        var options = new IrrigationConfiguration
        {
            SeasonalCoefficientsByBalanceGroup = new Dictionary<string, double[]>
            {
                ["LawnEast"] = [0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 0.4, 0.2]
            }
        };

        Assert.Equal(0.2, IrrigationService.GetSeasonalCoefficient(options, "lawneast", 1));
        Assert.Equal(0.4, IrrigationService.GetSeasonalCoefficient(options, "LawnEast", 11));
        Assert.Equal(1.0, IrrigationService.GetSeasonalCoefficient(options, "NewGroup", 7));
        Assert.Equal(1.0, IrrigationService.GetSeasonalCoefficient(options, "LawnEast", 13));
    }

    [Fact]
    public async Task CompletedDay_UsesItsLocalMonthCoefficientAcrossUtcMonthBoundary()
    {
        var start = DateTimeOffset.Parse("2026-09-30T21:05:00Z"); // 00:05 on Oct 1 in Bucharest.
        var end = DateTimeOffset.Parse("2026-10-01T20:55:00Z");
        var saved = JsonSerializer.Serialize(new
        {
            Observations = new[] { Obs(0, start), Obs(0, end) },
            BalanceDeficitMm = new Dictionary<string, double> { ["Garden"] = 0 },
            LastBalancedLocalDate = "2026-09-30",
            IrrigationRuns = Array.Empty<object>()
        });
        var stateFile = TempState(saved);
        try
        {
            var irrigation = CreateService(stateFile, new Dictionary<string, string?>());
            var options = irrigation.GetConfigurationSnapshot();
            options.TimeZoneId = "Europe/Bucharest";
            options.Zones = [new IrrigationZoneConfiguration { Id = "Z1", BalanceGroup = "Garden" }];
            var monthly = Enumerable.Repeat(1.0, 12).ToArray();
            monthly[9] = 0; // October; September deliberately remains at 1.0.
            options.SeasonalCoefficientsByBalanceGroup["Garden"] = monthly;
            irrigation.UpdateConfiguration(options);
            Assert.True(IrrigationService.CalculateEt0([Obs(0, start), Obs(0, end)], options) > 0);

            await irrigation.AddObservationAsync(Obs(0, DateTimeOffset.UtcNow), CancellationToken.None);
            var stored = await irrigation.GetStoredDataAsync(CancellationToken.None);
            var todayLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow,
                TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest")).DateTime);

            Assert.Equal(0, stored.BalanceDeficitMm["Garden"], 8);
            Assert.Equal(todayLocal.AddDays(-1), stored.LastBalancedLocalDate);
        }
        finally { Directory.Delete(Path.GetDirectoryName(stateFile)!, recursive: true); }
    }

    [Fact]
    public async Task IrrigationDisableInterlock_FailsClosedUntilValidStateAndAfterDisconnect()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:DataDirectory"] = directory
            }).Build();
            var store = new GatewayStateStore(configuration, new EphemeralDataProtectionProvider());
            var settings = store.GetSnapshot().Settings;
            settings.LoxoneServers.Add(new LoxoneSettings { Id = "mini", Name = "Mini", Host = "localhost", Port = 80, Username = "user", Password = "pass" });
            settings.Irrigation.IrrigationDisabledState = new IrrigationLoxoneStateMapping { ServerId = "mini", StateUuid = "switch-uuid" };
            await store.SaveAsync(settings, CancellationToken.None);

            var interlock = new IrrigationDisableStateService(store);
            Assert.True(interlock.GetStatus().Disabled);
            interlock.ReportConnection("mini", true);
            Assert.True(interlock.GetStatus().Disabled);
            interlock.ObserveState("mini", "switch-uuid", "not-a-bool");
            Assert.True(interlock.GetStatus().Disabled);
            interlock.ObserveState("mini", "switch-uuid", "false");
            Assert.False(interlock.GetStatus().Disabled);
            interlock.ObserveState("mini", "switch-uuid", "true");
            Assert.True(interlock.GetStatus().Disabled);
            settings.Irrigation.IrrigationDisabledState.StateUuid = "replacement-switch";
            await store.SaveAsync(settings, CancellationToken.None);
            Assert.True(interlock.GetStatus().Disabled);
            Assert.False(interlock.GetStatus().StateKnown);
            interlock.ObserveState("mini", "replacement-switch", "off");
            Assert.False(interlock.GetStatus().Disabled);
            interlock.ReportConnection("mini", false);
            Assert.True(interlock.GetStatus().Disabled);
            Assert.False(interlock.GetStatus().StateKnown);
        }
        finally { Directory.Delete(directory, recursive: true); }
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

            Assert.True(await service.RecordIrrigationAsync("V1", 600, "run-1", CancellationToken.None));
            Assert.True(await service.RecordIrrigationAsync("V1", 600, "run-1", CancellationToken.None));

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(stateFile));
            Assert.Equal(8, doc.RootElement.GetProperty("BalanceDeficitMm").GetProperty("LawnEast").GetDouble(), 6);
            Assert.Single(doc.RootElement.GetProperty("IrrigationRuns").EnumerateArray());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(stateFile)!, recursive: true);
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
            Assert.Equal("LawnEast", v1.BalanceGroup);
            Assert.Equal(23.3, v1.GroupApplicationRateMmPerHour, 6);
            Assert.True(v1.ShouldIrrigate);
            Assert.Contains("combined application rate", v1.DecisionSummary);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(stateFile)!, recursive: true);
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
            Directory.Delete(Path.GetDirectoryName(stateFile)!, recursive: true);
        }
    }

    private static WeatherObservation Obs(double rain, DateTimeOffset timestamp) =>
        new(20, 50, 1000, 5, 10000, rain, timestamp);

    private static string TempState(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"irrigation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "irrigation-state.json");
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
        var irrigation = new IrrigationConfiguration();
        configuration.GetSection("Api:IrrigationConfiguration").Bind(irrigation);
        irrigation.StateFile = stateFile;

        var dataDirectory = Path.GetDirectoryName(stateFile)!;
        var storeConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Gateway:DataDirectory"] = dataDirectory })
            .Build();
        var store = new GatewayStateStore(storeConfiguration, new EphemeralDataProtectionProvider());
        var settings = store.GetSnapshot().Settings;
        settings.Irrigation = irrigation;
        store.SaveAsync(settings, CancellationToken.None).GetAwaiter().GetResult();

        var http = new HttpClient(new ForecastHandler()) { BaseAddress = new Uri("https://example.test/") };
        return new IrrigationService(store, new OpenMeteoForecastClient(http));
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
