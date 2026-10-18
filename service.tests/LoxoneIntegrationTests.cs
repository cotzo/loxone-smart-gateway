using loxone.smart.gateway.Api.Gateway;
using Xunit;

namespace service.tests;

public sealed class LoxoneIntegrationTests
{
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
