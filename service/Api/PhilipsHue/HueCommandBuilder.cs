namespace loxone.smart.gateway.Api.PhilipsHue;

/// <summary>Converts compact mapping values into Hue CLIP command bodies.</summary>
public static class HueCommandBuilder
{
    public static string BuildCommandBody(int value, PhilipsHueLightType lightType, int transitionTime) => lightType switch
    {
        PhilipsHueLightType.RGB => value < 200000000 ? GetRgbCommand(value, transitionTime) : GetTunableCommand(value, transitionTime),
        PhilipsHueLightType.TUNABLE => GetTunableCommand(value == 50050050 ? 200504586 : value, transitionTime),
        PhilipsHueLightType.DIM => GetDimCommand(value, transitionTime),
        PhilipsHueLightType.ONOFF => GetOnOffCommand(value, transitionTime),
        _ => throw new ArgumentOutOfRangeException(nameof(lightType))
    };

    private static string GetOnOffCommand(int value, int transitionTime)
    {
        var on = value == 0 ? "false" : "true";
        return $"{{\"on\": {{\"on\": {on}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }

    private static string GetDimCommand(int value, int transitionTime) => value == 0
        ? GetOnOffCommand(0, transitionTime)
        : $"{{\"on\": {{\"on\": true}}, \"dimming\": {{\"brightness\": {value}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";

    private static string GetTunableCommand(int value, int transitionTime)
    {
        if (value == 0) return GetOnOffCommand(0, transitionTime);
        var brightness = (value - 200000000) / 10000;
        var temperature = value - 200000000 - brightness * 10000;
        temperature = 1000000 / temperature;
        return brightness == 0
            ? GetOnOffCommand(0, transitionTime)
            : $"{{\"on\": {{\"on\": true}}, \"dimming\": {{\"brightness\": {brightness}}}, \"color_temperature\": {{\"mirek\": {temperature}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }

    private static string GetRgbCommand(int value, int transitionTime)
    {
        if (value == 0) return GetOnOffCommand(value, transitionTime);
        var blueInput = value / 1000000;
        var greenInput = (value - blueInput * 1000000) / 1000;
        var redInput = value - blueInput * 1000000 - greenInput * 1000;
        double blue = blueInput, green = greenInput, red = redInput;
        var brightness = Math.Max(blue, Math.Max(green, red));
        blue /= 100;
        green /= 100;
        red /= 100;
        red = GammaCorrect(red);
        green = GammaCorrect(green);
        blue = GammaCorrect(blue);
        var x = red * 0.664511f + green * 0.154324f + blue * 0.162028f;
        var y = red * 0.283881f + green * 0.668433f + blue * 0.047685f;
        var z = red * 0.000088f + green * 0.072310f + blue * 0.986039f;
        var total = x + y + z;
        var cx = total == 0 ? 0 : x / total;
        var cy = total == 0 ? 0 : y / total;
        return $"{{\"color\": {{\"xy\": {{\"x\": {cx:F4}, \"y\": {cy:F4}}}}}, \"dimming\": {{\"brightness\": {brightness}}}, \"on\": {{\"on\": true}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }

    private static double GammaCorrect(double value) => value > 0.04045
        ? Math.Pow((value + 0.055) / 1.055, 2.4)
        : value / 12.92;
}
