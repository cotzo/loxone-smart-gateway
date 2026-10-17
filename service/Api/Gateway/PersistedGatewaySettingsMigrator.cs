using System.Text.Json;
using System.Text.Json.Nodes;

namespace loxone.smart.gateway.Api.Gateway;

/// <summary>
/// One-time compatibility shim for persisted settings. Remove this class once
/// installations no longer need to upgrade mappings from the legacy schema.
/// </summary>
public sealed class PersistedGatewaySettingsMigrator
{
    public MigrationResult Migrate(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new JsonException("Persisted gateway settings must be a JSON object.");
        if (Get(root, "mappings") is not JsonArray mappings)
            return new MigrationResult(json, false, 0);

        var migratedCount = 0;
        foreach (var node in mappings)
        {
            if (node is not JsonObject mapping) continue;
            var oldValueMode = GetString(mapping, "valueMode");
            var oldTuyaConverter = Get(mapping, "tuyaConverter") as JsonObject;
            var converter = Get(mapping, "converter") as JsonObject;
            var converterTypeNode = Get(converter, "type");
            var converterTypeExisting = GetString(converter, "type");
            var legacyHueColorMode = IsLegacyHueTextColorMapping(root, mapping, converterTypeExisting);
            if (oldValueMode is null && oldTuyaConverter is null && !legacyHueColorMode) continue;

            if (converter is null)
            {
                converter = new JsonObject();
                Set(mapping, "converter", converter);
            }

            // The old Hue mode and the intermediate Tuya converter coexisted;
            // preserve the explicit Tuya transform when it was configured.
            var oldTuyaMode = GetString(oldTuyaConverter, "mode")?.ToLowerInvariant();
            var converterType = oldTuyaMode switch
            {
                "lookup" => "lookup",
                "range" => "range",
                _ => oldValueMode?.ToLowerInvariant() switch
                {
                    "onoff" => "onOff",
                    "loxonedmx" or "packedcolor" => "packedColor",
                    "hsv" => "hsv",
                    "tunablewhite" => "tunableWhite",
                    _ => legacyHueColorMode ? GetHueTextColorConverter(mapping) : "direct"
                }
            };

            // A pre-existing new converter is authoritative; this also makes
            // partially migrated files safe to recover without losing edits.
            if (converterTypeNode is null || legacyHueColorMode && converterTypeExisting == "direct")
                Set(converter, "type", JsonValue.Create(converterType));

            if (oldTuyaMode is "lookup" or "range")
                CopyLegacyConverterFields(oldTuyaConverter!, converter);

            Remove(mapping, "valueMode");
            Remove(mapping, "tuyaConverter");
            migratedCount++;
        }

        return migratedCount == 0
            ? new MigrationResult(json, false, 0)
            : new MigrationResult(root.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)), true, migratedCount);
    }

    private static bool IsLegacyHueTextColorMapping(JsonObject root, JsonObject mapping, string? currentConverterType)
    {
        if (!string.Equals(GetString(mapping, "targetType"), "hue", StringComparison.OrdinalIgnoreCase) ||
            currentConverterType is not (null or "direct"))
            return false;

        var lightType = GetHueLightType(mapping);
        if (lightType is not ("RGB" or "TUNABLE")) return false;

        var serverId = GetString(mapping, "loxoneServerId");
        var stateUuid = GetString(mapping, "loxoneStateUuid");
        if (serverId is null || stateUuid is null || Get(root, "loxoneStateCatalogs") is not JsonObject catalogs ||
            Get(catalogs, serverId) is not JsonObject catalog || Get(catalog, "states") is not JsonArray states)
            return false;

        return states.OfType<JsonObject>().Any(state =>
            string.Equals(GetString(state, "uuid"), stateUuid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(GetString(state, "type"), "TextState", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetHueTextColorConverter(JsonObject mapping) =>
        string.Equals(GetHueLightType(mapping), "RGB", StringComparison.OrdinalIgnoreCase)
            ? "hsv"
            : "tunableWhite";

    private static string? GetHueLightType(JsonObject mapping)
    {
        var node = Get(mapping, "hueLightType");
        if (node is null) return null;
        if (node.GetValueKind() == JsonValueKind.String) return node.GetValue<string>();

        // System.Text.Json serializes PhilipsHueLightType as its numeric enum
        // value in persisted settings unless a string-enum converter is used.
        if (node.GetValueKind() == JsonValueKind.Number && node is JsonValue jsonValue && jsonValue.TryGetValue<int>(out var value))
            return value switch { 0 => "RGB", 1 => "TUNABLE", 2 => "DIM", 3 => "ONOFF", _ => null };

        return null;
    }

    private static string? GetString(JsonObject? value, string name)
    {
        var node = Get(value, name);
        return node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    }

    private static void CopyLegacyConverterFields(JsonObject legacy, JsonObject converter)
    {
        Copy(legacy, converter, "exactRules");
        Copy(legacy, converter, "rangeMinimum");
        Copy(legacy, converter, "rangeMaximum");
        Copy(legacy, converter, "insideOutputType");
        Copy(legacy, converter, "insideOutputValue");
        Copy(legacy, converter, "outsideOutputType");
        Copy(legacy, converter, "outsideOutputValue");
    }

    private static void Copy(JsonObject from, JsonObject to, string property)
    {
        if (Get(to, property) is null && Get(from, property) is { } value)
            Set(to, property, value.DeepClone());
    }

    private static JsonNode? Get(JsonObject? value, string name) =>
        value?.FirstOrDefault(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Set(JsonObject value, string name, JsonNode? node)
    {
        var existing = value.FirstOrDefault(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        value[existing ?? name] = node;
    }

    private static void Remove(JsonObject value, string name)
    {
        var existing = value.FirstOrDefault(property => property.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
        if (existing is not null) value.Remove(existing);
    }
}

public sealed record MigrationResult(string Json, bool Changed, int MappingsMigrated);
