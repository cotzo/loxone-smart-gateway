using loxone.smart.gateway.Api.Gateway;
using Xunit;

namespace service.tests;

public sealed class PersistedGatewaySettingsMigratorTests
{
    private readonly PersistedGatewaySettingsMigrator _migrator = new();

    [Theory]
    [InlineData("direct", "direct")]
    [InlineData("onOff", "onOff")]
    [InlineData("loxoneDmx", "packedColor")]
    public void Legacy_value_modes_are_upgraded(string legacyMode, string expectedType)
    {
        var result = _migrator.Migrate("{\"mappings\":[{\"valueMode\":\"" + legacyMode + "\"}]}");

        Assert.True(result.Changed);
        Assert.Equal(1, result.MappingsMigrated);
        Assert.Contains($"\"type\":\"{expectedType}\"", result.Json);
        Assert.DoesNotContain("valueMode", result.Json);
    }

    [Fact]
    public void Intermediate_tuya_lookup_is_migrated_to_shared_converter_and_is_idempotent()
    {
        const string legacy = """{"mappings":[{"valueMode":"direct","tuyaConverter":{"mode":"lookup","exactRules":[{"inputValue":1,"outputType":"string","outputValue":"reverse"}]}}]}""";

        var first = _migrator.Migrate(legacy);
        var second = _migrator.Migrate(first.Json);

        Assert.True(first.Changed);
        Assert.Equal(1, first.MappingsMigrated);
        Assert.Contains("\"type\":\"lookup\"", first.Json);
        Assert.Contains("\"outputValue\":\"reverse\"", first.Json);
        Assert.False(second.Changed);
        Assert.Equal(0, second.MappingsMigrated);
    }

    [Fact]
    public void Existing_new_converter_is_preserved_while_legacy_fields_are_removed()
    {
        const string json = """{"mappings":[{"valueMode":"onOff","converter":{"type":"range","rangeMinimum":2,"rangeMaximum":3}}]}""";

        var result = _migrator.Migrate(json);

        Assert.True(result.Changed);
        Assert.Contains("\"type\":\"range\"", result.Json);
        Assert.DoesNotContain("valueMode", result.Json);
    }

    [Theory]
    [InlineData("RGB", "hsv")]
    [InlineData("TUNABLE", "tunableWhite")]
    public void Legacy_hue_text_state_mappings_keep_their_implicit_color_conversion(string lightType, string expectedType)
    {
        var json = $"{{\"loxoneStateCatalogs\":{{\"server\":{{\"states\":[{{\"uuid\":\"state\",\"type\":\"TextState\"}}]}}}},\"mappings\":[{{\"targetType\":\"hue\",\"hueLightType\":\"{lightType}\",\"loxoneServerId\":\"server\",\"loxoneStateUuid\":\"state\",\"converter\":{{\"type\":\"direct\"}}}}]}}";

        var result = _migrator.Migrate(json);

        Assert.True(result.Changed);
        Assert.Contains($"\"type\":\"{expectedType}\"", result.Json);
    }

    [Fact]
    public void Numeric_hue_light_type_in_persisted_settings_is_supported()
    {
        const string json = """{"loxoneStateCatalogs":{"server":{"states":[{"uuid":"state","type":"TextState"}]}},"mappings":[{"targetType":"hue","hueLightType":0,"loxoneServerId":"server","loxoneStateUuid":"state","converter":{"type":"direct"}}]}""";

        var result = _migrator.Migrate(json);

        Assert.True(result.Changed);
        Assert.Contains("\"type\":\"hsv\"", result.Json);
    }
}
