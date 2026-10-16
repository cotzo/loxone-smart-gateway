using loxone.smart.gateway.Api.Gateway;
using Xunit;

namespace service.tests;

public sealed class LoxoneIntegrationTests
{
    [Theory]
    [InlineData("hsv(0,0,0)")]
    [InlineData("hsv(0, 0, 0)")]
    [InlineData("HSV(0,0,0)")]
    public void ParsesZeroHsvAsOff(string raw)
    {
        Assert.True(LoxoneIntegration.TryParseStateValue(raw, out var value));
        Assert.Equal(0, value);
    }

    [Theory]
    [InlineData("hsv(120,50,80)")]
    [InlineData("hsv(0,0,1)")]
    public void DoesNotTreatNonzeroHsvAsOff(string raw)
    {
        Assert.False(LoxoneIntegration.TryParseStateValue(raw, out _));
    }
}
