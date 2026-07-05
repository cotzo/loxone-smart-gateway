using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Serilog;

namespace loxone.smart.gateway.Api.PhilipsHue;

public class PhilipsHueMessageSender
    : BackgroundService
{
    private readonly PhilipsHueMetrics _metrics;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ConcurrentQueue<PhilipsHueRequestModel> _requestModels = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly PhilipsHueConfiguration _configuration = new();

    private DateTime _lastLightCommand = DateTime.MinValue;
    private DateTime _lastGroupedLightCommand = DateTime.MinValue;

    private static readonly TimeSpan LightRateLimit = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan GroupedLightRateLimit = TimeSpan.FromMilliseconds(1000);
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);

    public PhilipsHueMessageSender(IConfiguration config, PhilipsHueMetrics metrics, IHttpClientFactory httpClientFactory)
    {
        _metrics = metrics;
        _httpClientFactory = httpClientFactory;
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
        var ids = model.Id.Split(';');

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
            _signal.Release();
        }
    }

    private async Task StartBackgroundWork(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested && _requestModels.IsEmpty)
            {
                return;
            }

            // Block until a command is enqueued instead of polling, so commands are dispatched
            // immediately (previously up to 50 ms of added latency per command). On shutdown we
            // fall through and drain whatever is still queued.
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down: loop again to drain the queue, then exit.
                    continue;
                }
            }

            if (!_requestModels.TryDequeue(out var model))
            {
                continue;
            }

            if (model.Retries < 10)
            {
                await EnforceRateLimit(model.ResourceType);

                try
                {
                    var result = await ProcessMessage(model);

                    if (!result)
                    {
                        model.Retries++;
                        _requestModels.Enqueue(model);
                        _signal.Release();
                    }
                }
                catch (Exception ex)
                {
                    model.Retries++;

                    Log.Error(ex, "Error while processing message");
                    _requestModels.Enqueue(model);
                    _signal.Release();
                }
            }
            else
            {
                Log.Error("Failed to process message {model} for 10 times. Removing from queue", model);
            }
        }
    }

    private async Task EnforceRateLimit(string resourceType)
    {
        var now = DateTime.UtcNow;

        if (resourceType == "grouped_light")
        {
            var elapsed = now - _lastGroupedLightCommand;
            if (elapsed < GroupedLightRateLimit)
                await Task.Delay(GroupedLightRateLimit - elapsed);
            _lastGroupedLightCommand = DateTime.UtcNow;
        }
        else
        {
            var elapsed = now - _lastLightCommand;
            if (elapsed < LightRateLimit)
                await Task.Delay(LightRateLimit - elapsed);
            _lastLightCommand = DateTime.UtcNow;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ConfigureAllLightsPowerOnBehavior();

        // Process the command queue and keep the bridge connection warm in parallel.
        await Task.WhenAll(
            StartBackgroundWork(stoppingToken),
            KeepConnectionWarm(stoppingToken));
    }

    private async Task KeepConnectionWarm(CancellationToken cancellationToken)
    {
        // Periodically ping the bridge so the pooled TLS connection never goes idle. This keeps
        // every light command handshake-free even after hours of inactivity, and re-establishes
        // the connection proactively after a bridge reboot or network blip.
        try
        {
            using var timer = new PeriodicTimer(KeepAliveInterval);

            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    var client = CreateHttpClient();
                    var url = $"https://{_configuration.IP}/clip/v2/resource/bridge";
                    using var response = await client.GetAsync(url, cancellationToken);
                }
                catch (Exception ex)
                {
                    // A failed ping is harmless: the next real command simply reconnects.
                    Log.Debug(ex, "Hue keep-alive ping failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
    }

    private async Task ConfigureAllLightsPowerOnBehavior()
    {
        try
        {
            var client = CreateHttpClient();
            var url = $"https://{_configuration.IP}/clip/v2/resource/light";
            var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("Failed to fetch lights for power-on configuration: {statusCode}", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var lights = doc.RootElement.GetProperty("data");

            foreach (var light in lights.EnumerateArray())
            {
                var lightId = light.GetProperty("id").GetString();
                if (string.IsNullOrEmpty(lightId))
                    continue;

                await SetPowerOnBehavior(lightId);
                await Task.Delay(LightRateLimit);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to configure power-on behavior for all lights");
        }
    }

    private async Task<bool> ProcessMessage(PhilipsHueRequestModel model)
    {
        var commandBody = string.Empty;

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
            Log.Error("Invalid Request. No Command created::: {model}", model);
            return true;
        }

        Log.Information("Body: {commandBody}. Resource Type: {resource}. Id: {id}", commandBody, model.ResourceType, model.Id);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var client = CreateHttpClient();
            var url = $"https://{_configuration.IP}/clip/v2/resource/{model.ResourceType}/{model.Id}";

            var response = await client.PutAsync(url,
                new StringContent(commandBody, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                Log.Error("Response status code: {statusCode}. Response Body: {body}. Request URL: {url}", response.StatusCode, await response.Content.ReadAsStringAsync(), url);
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

    private async Task SetPowerOnBehavior(string lightId)
    {
        try
        {
            var client = CreateHttpClient();
            var url = $"https://{_configuration.IP}/clip/v2/resource/light/{lightId}";
            var body = new StringContent("{\"powerup\": {\"preset\": \"powerfail\"}}", Encoding.UTF8, "application/json");

            var response = await client.PutAsync(url, body);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("Set power-on behavior to powerfail for light {lightId}", lightId);
            }
            else
            {
                Log.Warning("Failed to set power-on behavior for light {lightId}: {statusCode}", lightId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to set power-on behavior for light {lightId}", lightId);
        }
    }

    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient("PhilipsHue");
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("hue-application-key", _configuration.AccessKey);
        return client;
    }

    private static string GetOnOffCommand(int value, int transitionTime)
    {
        var on = value == 0 ? "false" : "true";

        return $"{{\"on\": {{\"on\": {on}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }

    private static string GetDimCommand(int value, int transitionTime)
    {
        // Check if brightness is set to 0 and leave early
        return 0 == value ?
            GetOnOffCommand(0, transitionTime) :
            $"{{\"on\": {{\"on\": true}}, \"dimming\": {{\"brightness\": {value}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }

    private static string GetTunableCommand(int value, int transitionTime)
    {
        if (value == 0)
            return GetOnOffCommand(0, transitionTime);

        var brightness = (value - 200000000) / 10000; // 0-100
        var temperature = value - 200000000 - brightness * 10000; // Kelvin 2700 - 6500

        temperature = 1000000 / temperature; // 154 - 370

        // Check if input value was set to 0 or brightness is set to 0
        return 0 == brightness ?
            GetOnOffCommand(0, transitionTime) :
            $"{{\"on\": {{\"on\": true}}, \"dimming\": {{\"brightness\": {brightness}}}, \"color_temperature\": {{\"mirek\": {temperature}}}, \"dynamics\": {{\"duration\": {transitionTime}}}}}";
    }

    private static string GetRgbCommand(int value, int transitionTime)
    {
        // Check if brightness is set to 0 and leave early
        if (0 == value)
        {
            return GetOnOffCommand(value, transitionTime);
        }

        var blueInput = value / 1000000;
        var greenInput = (value - blueInput * 1000000) / 1000;
        var redInput = value - blueInput * 1000000 - greenInput * 1000;

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
        if (x + y + z == 0)
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
