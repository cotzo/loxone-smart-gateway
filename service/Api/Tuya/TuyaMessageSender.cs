using System.Collections.Concurrent;
using System.Diagnostics;
using Serilog;

namespace loxone.smart.gateway.Api.Tuya;

public class TuyaMessageSender : BackgroundService
{
    private readonly TuyaMetrics _metrics;
    private readonly ConcurrentQueue<TuyaRequestModel> _requestModels = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TuyaConfiguration _configuration = new();

    public TuyaMessageSender(IConfiguration config, TuyaMetrics metrics)
    {
        _metrics = metrics;
        config.GetSection("Api:TuyaConfiguration").Bind(_configuration);

        foreach (var device in _configuration.Devices)
        {
            if (string.IsNullOrEmpty(device.Name) ||
                string.IsNullOrEmpty(device.Id) ||
                string.IsNullOrEmpty(device.IP) ||
                string.IsNullOrEmpty(device.LocalKey) ||
                device.Version is not ("3.4" or "3.5"))
            {
                throw new ApplicationException(
                    $"Tuya device configuration is incomplete for '{device.Name}': Name, Id, IP, LocalKey and Version (3.4 or 3.5) are required.");
            }
        }

        if (_configuration.Devices.Count == 0)
        {
            Log.Information("No Tuya devices configured");
        }
    }

    public void AddToQueue(TuyaRequestModel model)
    {
        _requestModels.Enqueue(model);
        _signal.Release();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            if (stoppingToken.IsCancellationRequested && _requestModels.IsEmpty)
            {
                return;
            }

            // Block until a command is enqueued; on shutdown fall through
            // and drain whatever is still queued.
            if (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _signal.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
            }

            if (!_requestModels.TryDequeue(out var model))
            {
                continue;
            }

            if (model.Retries < 10)
            {
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

    private async Task<bool> ProcessMessage(TuyaRequestModel model)
    {
        var device = _configuration.Devices
            .FirstOrDefault(d => d.Name.Equals(model.Name, StringComparison.OrdinalIgnoreCase));

        if (device == null)
        {
            Log.Error("Unknown Tuya device. No command sent::: {model}", model);
            return true;
        }

        Log.Information("Sending dp {dp} = {value} to Tuya device {device} ({ip})", model.Dp, model.Value.GetRawText(), device.Name, device.IP);

        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var connection = new TuyaLocalConnection(device);
            await connection.ConnectAsync(CancellationToken.None);
            await connection.SetDataPointAsync(model.Dp, model.Value, CancellationToken.None);
        }
        finally
        {
            stopwatch.Stop();
            _metrics.RecordDuration(stopwatch.ElapsedMilliseconds, device.Name);
        }

        return true;
    }
}
