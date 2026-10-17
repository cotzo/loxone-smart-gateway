using loxone.smart.gateway.Api.Gateway;
using Xunit;

namespace service.tests;

public sealed class LoxoneIntegrationTests
{
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
