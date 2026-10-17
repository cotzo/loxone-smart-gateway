using System.Globalization;
using System.Text.Json;

namespace loxone.smart.gateway.Api.Gateway;

public static class MappingValueConverter
{
    public static JsonElement Convert(DeviceMapping mapping, string sourceValue)
    {
        var converter = mapping.Converter ?? new MappingValueConverterSettings();
        return converter.Type switch
        {
            // Older gateway versions decoded Loxone's HSV and tunable-white
            // strings before applying the mapping's value mode. Keep that
            // behavior for mappings persisted as "direct" as well.
            "direct" or "packedColor" => Number(DecodeLegacyLoxoneValue(sourceValue).ToString("G", CultureInfo.InvariantCulture)),
            "onOff" => ParseOutput("boolean", (ParseNumber(sourceValue) != 0).ToString().ToLowerInvariant()),
            "hsv" => ConvertColor(sourceValue, "hsv("),
            "tunableWhite" => ConvertColor(sourceValue, "temp("),
            "lookup" => ConvertLookup(converter, ParseNumber(sourceValue)),
            "range" => ConvertRange(converter, ParseNumber(sourceValue)),
            _ => throw new InvalidOperationException($"Unknown value converter type '{converter.Type}'.")
        };
    }

    public static JsonElement ParseOutput(string type, string value)
    {
        using var document = type switch
        {
            "number" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
                => JsonDocument.Parse(number.ToString("G", CultureInfo.InvariantCulture)),
            "boolean" when bool.TryParse(value, out var boolean)
                => JsonDocument.Parse(boolean ? "true" : "false"),
            "string" => JsonDocument.Parse(JsonSerializer.Serialize(value)),
            _ => throw new ArgumentException($"Invalid converter output: '{value}' is not a {type}.")
        };
        return document.RootElement.Clone();
    }

    private static JsonElement ConvertLookup(MappingValueConverterSettings converter, double input)
    {
        var rule = converter.ExactRules.FirstOrDefault(x => x.InputValue.Equals(input));
        if (rule is null)
            throw new InvalidOperationException($"No conversion rule matches input {input.ToString("G", CultureInfo.InvariantCulture)}.");
        return ParseOutput(rule.OutputType, rule.OutputValue);
    }

    private static JsonElement ConvertRange(MappingValueConverterSettings converter, double input)
    {
        if (!double.IsFinite(converter.RangeMinimum) || !double.IsFinite(converter.RangeMaximum) || converter.RangeMinimum > converter.RangeMaximum)
            throw new InvalidOperationException("Range minimum must be finite and no greater than the maximum.");
        var inside = input >= converter.RangeMinimum && input <= converter.RangeMaximum;
        return inside
            ? ParseOutput(converter.InsideOutputType, converter.InsideOutputValue)
            : ParseOutput(converter.OutsideOutputType, converter.OutsideOutputValue);
    }

    private static JsonElement Number(string sourceValue) =>
        ParseOutput("number", ParseNumber(sourceValue).ToString("G", CultureInfo.InvariantCulture));

    private static double DecodeLegacyLoxoneValue(string raw) =>
        (raw.StartsWith("hsv(", StringComparison.OrdinalIgnoreCase) || raw.StartsWith("temp(", StringComparison.OrdinalIgnoreCase)) &&
        LoxoneIntegration.TryParseStateValue(raw, out var converted)
            ? converted
            : ParseNumber(raw);

    private static double ParseNumber(string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw new InvalidOperationException($"Converter '{raw}' requires a finite numeric source value.");

    private static JsonElement ConvertColor(string raw, string expectedPrefix)
    {
        // A Loxone color TextState can report either its formatted value or
        // the packed numeric representation depending on event-table timing.
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var packed) && double.IsFinite(packed))
            return Number(packed.ToString("G", CultureInfo.InvariantCulture));
        if (!raw.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) || !LoxoneIntegration.TryParseStateValue(raw, out var converted))
            throw new InvalidOperationException($"Source value is not a valid {expectedPrefix.TrimEnd('(')} value.");
        return Number(converted.ToString("G", CultureInfo.InvariantCulture));
    }
}
