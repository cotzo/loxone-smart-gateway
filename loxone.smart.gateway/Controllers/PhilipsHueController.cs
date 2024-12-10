using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("[controller]")]
public class PhilipsHueController(ILogger<PhilipsHueController> logger) : ControllerBase
{
    [HttpPost("{id}")]
    public async Task<IActionResult> SetLights(string id, [FromBody] int value, [FromQuery] string ip, [FromQuery] string accessKey,
        [FromQuery] PhilipsHueLightType lightType, [FromQuery]string resourceType, [FromQuery] int transitionTime)
    {
        logger.LogInformation($"New Request::: id: {id}, value: {value}, ip: {ip}, accessKey: {accessKey}, lightType: {lightType}, resourceType: {resourceType}, transitionTime: {transitionTime}  ");
        
        string commandBody = string.Empty;

        switch (lightType)
        {
            case PhilipsHueLightType.RGB:
                commandBody = value < 200000000
                    ? GetRgbCommand(value, transitionTime)
                    : GetTunableCommand(value, transitionTime);
                break;
            case PhilipsHueLightType.TUNABLE:
                if (value is >= 200000000 or 0)
                {
                    commandBody = GetTunableCommand(value, transitionTime);
                }

                break;
            case PhilipsHueLightType.DIM:
                commandBody = GetDimCommand(value, transitionTime);
                break;
            case PhilipsHueLightType.ONOFF:
                commandBody = GetOnOffCommand(value, transitionTime);
                break;
        }

        if (string.IsNullOrEmpty(commandBody))
            return BadRequest("No command created");

        logger.LogInformation($"Body: {commandBody}");
        
        using var handler = new HttpClientHandlerInsecure();
        using HttpClient client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("hue-application-key", accessKey);
        await client.PutAsync($"https://{ip}/clip/v2/resource/{resourceType}/{id}",
            new StringContent(commandBody));

        return Ok();
    }

    private static string GetOnOffCommand(int value, int transitionTime)
    {
        if (value == 0)
        {
            return $"{{\"on\": {{\"on\": false}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
        }

        return $"{{\"on\": {{\"on\": true}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }

    private static string GetDimCommand(int value, int transitionTime)
    {
        // Check if brightness is set to 0 and leave early
        if (0 == value)
        {
            return GetOnOffCommand(0, transitionTime);
        }
        else
        {
            return
                $"{{\"on\": {{\"on\": true}}, \"dimming\": {{\"brightness\": {value}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
        }
    }

    private static string GetTunableCommand(int value, int transitionTime)
    {
        var brightness = (value - 200000000) / 10000; // 0-100
        var temperature = (value - 200000000) - (brightness * 10000); // Kelvin 2700 - 6500

        temperature = 1000000 / temperature; // 154 - 370

        // Check if input value was set to 0 or brightness is set to 0
        if ((0 == value) || (0 == brightness))
        {
            return GetOnOffCommand(0, transitionTime);
        }
        else
        {
            return
                $"{{\"on\": {{\"on\": true}}, \"dimming\": {{\"brightness\": {brightness}}}, \"color_temperature\": {{\"mirek\": {temperature}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
        }
    }

    private string GetRgbCommand(int value, int transitionTime)
    {
        // Check if brightness is set to 0 and leave early
        if (0 == value)
        {
            return GetOnOffCommand(value, transitionTime);
        }

        int blueInput = value / 1000000;
        int greenInput = (value - (blueInput * 1000000)) / 1000;
        int redInput = value - (blueInput * 1000000) - (greenInput * 1000);

        logger.LogInformation($"RGB Identified: r: {redInput}, g: {greenInput}, b: {blueInput}");

        double blue = blueInput;
        double green = greenInput;
        double red = redInput;
        
        double cx;
        double cy;

        var bri = blue;
        if (bri < green)
        {
            bri = green;
        }

        if (bri < red)
        {
            bri = red;
        }

        blue /= 100;
        green /= 100;
        red /= 100;

        // Apply gamma correction
        if (red > 0.04045)
        {
            red = Math.Pow((red + 0.055) / 1.055, 2.4);
        }
        else
        {
            red /= 12.92;
        }

        if (green > 0.04045)
        {
            green = Math.Pow((green + 0.055) / 1.055, 2.4);
        }
        else
        {
            green /= 12.92;
        }

        if (blue > 0.04045)
        {
            blue = Math.Pow((blue + 0.055) / 1.055, 2.4);
        }
        else
        {
            blue /= 12.92;
        }

        // Wide gamut conversion D65
        var x = red * 0.664511f + green * 0.154324f + blue * 0.162028f;
        var y = red * 0.283881f + green * 0.668433f + blue * 0.047685f;
        var z = red * 0.000088f + green * 0.072310f + blue * 0.986039f;

        // Calculate xy and bri
        if ((x + y + z) == 0)
        {
            cx = 0;
            cy = 0;
        }
        else
        {
            // round to 4 decimal max (=api max size)
            cx = x / (x + y + z);
            cy = y / (x + y + z);
        }

        return
            $"{{\"color\": {{\"xy\": {{\"x\": {cx:F4}, \"y\": {cy:F4}}}}}, \"dimming\": {{\"brightness\": {bri}}}, \"on\": {{\"on\": true}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }
}

public enum PhilipsHueLightType
{
    RGB,
    TUNABLE,
    DIM,
    ONOFF
}