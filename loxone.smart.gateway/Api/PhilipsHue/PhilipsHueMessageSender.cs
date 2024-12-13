using System.Collections.Concurrent;
using System.Diagnostics;

namespace loxone.smart.gateway.Api.PhilipsHue;

public class PhilipsHueMessageSender
    : BackgroundService
{
    private readonly ILogger<PhilipsHueMessageSender> _logger;
    private readonly PhilipsHueMetrics _metrics;
    private readonly ConcurrentQueue<PhilipsHueRequestModel> _requestModels = new();
    private readonly PhilipsHueConfiguration _configuration = new();

    public PhilipsHueMessageSender(ILogger<PhilipsHueMessageSender> logger, IConfiguration config, PhilipsHueMetrics metrics)
    {
        _logger = logger;
        _metrics = metrics;
        config.GetSection("Api:PhilipsHueConfiguration").Bind(_configuration);

        if (_configuration == null ||
            string.IsNullOrEmpty(_configuration.AccessKey) ||
            string.IsNullOrEmpty(_configuration.IP))
        {
            throw new ApplicationException("PhilipsHue configuration is missing.");
        }
    }
    
    public void AddToQueue(PhilipsHueRequestModel model)
    {
        string[] ids = model.Id.Split(';');

        foreach (var id in ids)
        {
            _requestModels.Enqueue(new PhilipsHueRequestModel
            {
                Id = id,
                LightType = model.LightType,
                ResourceType = model.ResourceType,
                TransitionTime = model.TransitionTime,
                Value = model.Value
            });
        }
    }
    
    private async Task StartBackgroundWork(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested && _requestModels.Count == 0)
            {
                return;
            }   
            
            if (_requestModels.TryDequeue(out var model))
            {
                if (model.Retries < 10)
                {
                    try
                    {
                        var result = await ProcessMessage(model);
                        
                        if (!result)
                            _requestModels.Enqueue(model);
                    }
                    catch (Exception ex)
                    {
                        model.Retries++;
                    
                        _logger.LogError(ex, ex.Message);
                        _requestModels.Enqueue(model);
                    }
                }
                else
                {
                    _logger.LogError($"Failed to process message {model} for 10 times. Removing from queue");
                }
            }
            Thread.Sleep(50);
        }
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        await StartBackgroundWork(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
    }

    private async Task<bool> ProcessMessage(PhilipsHueRequestModel model)
    {
        string commandBody = string.Empty;

        switch (model.LightType)
        {
            case PhilipsHueLightType.RGB:
                commandBody = model.Value < 200000000
                    ? GetRgbCommand(model.Value, model.TransitionTime)
                    : GetTunableCommand(model.Value, model.TransitionTime);
                break;
            case PhilipsHueLightType.TUNABLE:
                // hack because on Loxone Lighting Controller All On, he is sending RGB 50% brightness even for non RGB lighting
                if (model.Value == 50050050)
                {
                    model.Value = 200504586;
                }
                if (model.Value is >= 200000000 or 0)
                {
                    commandBody = GetTunableCommand(model.Value, model.TransitionTime);
                }

                break;
            case PhilipsHueLightType.DIM:
                commandBody = GetDimCommand(model.Value, model.TransitionTime);
                break;
            case PhilipsHueLightType.ONOFF:
                commandBody = GetOnOffCommand(model.Value, model.TransitionTime);
                break;
        }

        if (string.IsNullOrEmpty(commandBody))
        {
            _logger.LogError(
                $"Invalid Request. No Command created::: {model}");
            return true;
        }


        _logger.LogInformation($"Body: {commandBody}. Resource Type: {model.ResourceType}. Id: {model.Id}");

        Stopwatch stopwatch = Stopwatch.StartNew();

        try
        {
            using var handler = new HttpClientHandlerInsecure();
            using HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("hue-application-key", _configuration.AccessKey);
            string url = $"https://{_configuration.IP}/clip/v2/resource/{model.ResourceType}/{model.Id}";
            
            HttpResponseMessage response = await client.PutAsync(url,
                new StringContent(commandBody));

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError($"Response status code: {response.StatusCode}. Response Body: {await response.Content.ReadAsStringAsync()}. Request URL: {url}");
                return false;
            }
        }
        finally
        {
            stopwatch.Stop();
            _metrics.RecordDuration(stopwatch.ElapsedMilliseconds, model.ResourceType);
        }

        return true;
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
        if (value == 0)
            return GetOnOffCommand(0, transitionTime);
        
        var brightness = (value - 200000000) / 10000; // 0-100
        var temperature = (value - 200000000) - (brightness * 10000); // Kelvin 2700 - 6500

        temperature = 1000000 / temperature; // 154 - 370

        // Check if input value was set to 0 or brightness is set to 0
        if (0 == brightness)
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