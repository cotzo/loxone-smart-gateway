using loxone.smart.gateway.Api.Gateway;
using Xunit;

namespace service.tests;

public sealed class MappingValueConverterTests
{
    [Fact]
    public void Lookup_maps_numeric_switch_values_to_strings()
    {
        var mapping = new DeviceMapping
        {
            Converter = new MappingValueConverterSettings
            {
                Type = "lookup",
                ExactRules =
                [
                    new() { InputValue = 0, OutputType = "string", OutputValue = "forward" },
                    new() { InputValue = 1, OutputType = "string", OutputValue = "reverse" }
                ]
            }
        };

        Assert.Equal("forward", MappingValueConverter.Convert(mapping, "0").GetString());
        Assert.Equal("reverse", MappingValueConverter.Convert(mapping, "1").GetString());
    }

    [Fact]
    public void Range_uses_inclusive_bounds_and_typed_boolean_outputs()
    {
        var mapping = new DeviceMapping
        {
            Converter = new MappingValueConverterSettings { Type = "range", RangeMinimum = 10, RangeMaximum = 20 }
        };

        Assert.True(MappingValueConverter.Convert(mapping, "10").GetBoolean());
        Assert.True(MappingValueConverter.Convert(mapping, "15").GetBoolean());
        Assert.True(MappingValueConverter.Convert(mapping, "20").GetBoolean());
        Assert.False(MappingValueConverter.Convert(mapping, "9.99").GetBoolean());
        Assert.False(MappingValueConverter.Convert(mapping, "20.01").GetBoolean());
    }

    [Fact]
    public void Lookup_without_match_fails_instead_of_sending_an_unintended_value()
    {
        var mapping = new DeviceMapping
        {
            Converter = new MappingValueConverterSettings
            {
                Type = "lookup",
                ExactRules = [new() { InputValue = 1, OutputType = "string", OutputValue = "reverse" }]
            }
        };

        Assert.Throws<InvalidOperationException>(() => MappingValueConverter.Convert(mapping, "0"));
    }

    [Fact]
    public void Hsv_conversion_occurs_in_the_conversion_stage()
    {
        var mapping = new DeviceMapping { Converter = new MappingValueConverterSettings { Type = "hsv" } };

        Assert.Equal(100, MappingValueConverter.Convert(mapping, "hsv(0,100,100)").GetInt32());
    }

    [Theory]
    [InlineData("hsv(0,0,0)", 0)]
    [InlineData("hsv(0,0,100)", 100_100_100)]
    [InlineData("temp(0,2700)", 200_002_700)]
    public void Legacy_direct_converter_still_decodes_formatted_loxone_color_states(string source, int expected)
    {
        var mapping = new DeviceMapping { Converter = new MappingValueConverterSettings { Type = "direct" } };

        Assert.Equal(expected, MappingValueConverter.Convert(mapping, source).GetInt32());
    }

    [Theory]
    [InlineData("hsv", "100")]
    [InlineData("tunableWhite", "200004344")]
    public void Explicit_color_converters_accept_packed_numeric_source_values(string converterType, string source)
    {
        var mapping = new DeviceMapping { Converter = new MappingValueConverterSettings { Type = converterType } };

        Assert.Equal(int.Parse(source), MappingValueConverter.Convert(mapping, source).GetInt32());
    }
}
