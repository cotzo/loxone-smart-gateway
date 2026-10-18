using loxone.smart.gateway.Api.Gateway;
using Xunit;

namespace service.tests;

public sealed class LoxoneIntegrationTests
{
    [Fact]
    public void IrrigationCurrentZone_UsesLoxoneZoneIdRatherThanConfigurationPosition()
    {
        var zones = new[]
        {
            new loxone.smart.gateway.Api.Irrigation.IrrigationZoneConfiguration { Id = "gateway-2", LoxoneZoneId = "2" },
            new loxone.smart.gateway.Api.Irrigation.IrrigationZoneConfiguration { Id = "gateway-0", LoxoneZoneId = "0" },
            new loxone.smart.gateway.Api.Irrigation.IrrigationZoneConfiguration { Id = "gateway-1", LoxoneZoneId = "1" }
        };

        Assert.Equal(new[] { "gateway-1" },
            loxone.smart.gateway.Api.Irrigation.IrrigationLoxoneService.ResolveCurrentZoneIds(true, 1, zones));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, -1)]
    [InlineData(true, 7)]
    public void IrrigationCurrentZone_OffOrUnknownIndexDoesNotGuessAZone(bool active, int currentZone)
    {
        var zones = new[]
        {
            new loxone.smart.gateway.Api.Irrigation.IrrigationZoneConfiguration { Id = "gateway-0", LoxoneZoneId = "0" },
            new loxone.smart.gateway.Api.Irrigation.IrrigationZoneConfiguration { Id = "gateway-1", LoxoneZoneId = "1" }
        };

        Assert.Empty(loxone.smart.gateway.Api.Irrigation.IrrigationLoxoneService.ResolveCurrentZoneIds(active, currentZone, zones));
    }

    [Fact]
    public void IrrigationCurrentZoneEightTracksAllZones()
    {
        var zones = new[]
        {
            new loxone.smart.gateway.Api.Irrigation.IrrigationZoneConfiguration { Id = "gateway-0", LoxoneZoneId = "0" },
            new loxone.smart.gateway.Api.Irrigation.IrrigationZoneConfiguration { Id = "gateway-1", LoxoneZoneId = "1" }
        };

        Assert.Equal(new[] { "gateway-0", "gateway-1" },
            loxone.smart.gateway.Api.Irrigation.IrrigationLoxoneService.ResolveCurrentZoneIds(true, 8, zones));
    }
    [Fact]
    public void BuildsBatchIrrigationDurationCommandWithZeroBasedZoneIds()
    {
        var command = LoxoneIntegration.BuildIrrigationDurationsCommand("control-uuid", new Dictionary<string, int>
        {
            ["1"] = 240,
            ["0"] = 120
        });

        Assert.Equal("jdev/sps/io/control-uuid/setDurations/0=120/1=240", command);
    }

    [Theory]
    [InlineData("", "0")]
    [InlineData("control", "8")]
    [InlineData("control", "not-a-zone")]
    public void RejectsInvalidIrrigationDurationCommands(string controlUuid, string zoneId)
    {
        Assert.ThrowsAny<ArgumentException>(() => LoxoneIntegration.BuildIrrigationDurationsCommand(
            controlUuid, new Dictionary<string, int> { [zoneId] = 120 }));
    }

    [Theory]
    [InlineData("false", "-1", true)]
    [InlineData("0", "-1", true)]
    [InlineData("true", "-1", false)]
    [InlineData("false", "0", false)]
    [InlineData("unknown", "-1", false)]
    public void OnlyConsidersBlockIdleWhenBothStatesConfirmIt(string active, string currentZone, bool expected)
    {
        Assert.Equal(expected, loxone.smart.gateway.Api.Irrigation.IrrigationLoxoneService.IsInactiveForSync(active, currentZone));
    }

    [Fact]
    public void NormalizesWatchedStateUuidsToMatchLoxoneEventUuids()
    {
        var watched = LoxoneIntegration.NormalizeStateUuids(
            ["2105c36a-029f-101c-18ff-0c30f195a087", "217778eb018e358905ff4d9d810bc0d6"]);

        Assert.Contains("2105c36a029f101c18ff0c30f195a087", watched);
        Assert.Contains("217778eb018e358905ff4d9d810bc0d6", watched);
        Assert.Equal(2, watched.Count);
    }

    [Theory]
    [InlineData("hsv(0,0,0)", 0)]
    [InlineData("hsv(0, 0, 100)", 100_100_100)]
    [InlineData("HSV(0,100,100)", 100)]
    [InlineData("hsv(120,100,100)", 100_000)]
    [InlineData("hsv(240,100,100)", 100_000_000)]
    [InlineData("hsv(360,100,100)", 100)]
    [InlineData("hsv(120,50,80)", 40_080_040)]
    public void ParsesHsvToPackedRgb(string raw, double expected)
    {
        Assert.True(LoxoneIntegration.TryParseStateValue(raw, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("hsv(361,50,80)")]
    [InlineData("hsv(120,101,80)")]
    [InlineData("hsv(120,50,-1)")]
    [InlineData("hsv(NaN,50,80)")]
    [InlineData("hsv(120,50,80,20)")]
    public void RejectsMalformedOrOutOfRangeHsv(string raw)
    {
        Assert.False(LoxoneIntegration.TryParseStateValue(raw, out _));
    }
}
