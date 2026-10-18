using loxone.smart.gateway.Api.PhilipsHue;
using Serilog;

namespace loxone.smart.gateway.Api.Gateway;

internal sealed record GatewayCommand(DeviceMapping Mapping, string SourceValue, DateTimeOffset ReceivedAt);

/// <summary>
/// Dispatches current-state mappings through independent provider lanes. Each Hue bridge has
/// separate light and grouped-light queues; Tuya has its own queue, independent of Hue traffic.
/// </summary>
internal sealed class GatewayCommandDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan HueLightInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HueGroupedLightInterval = TimeSpan.FromMilliseconds(1000);
    private readonly GatewayStateStore _store;
    private readonly DeviceIntegrations _devices;
    private readonly GatewayMetrics _metrics;
    private readonly CancellationTokenSource _stop;
    private readonly object _queueSync = new();
    private readonly Dictionary<string, LatestValueCommandQueue<GatewayCommand>> _queues = new(StringComparer.OrdinalIgnoreCase);

    public GatewayCommandDispatcher(GatewayStateStore store, DeviceIntegrations devices, GatewayMetrics metrics, CancellationToken cancellationToken)
    {
        _store = store;
        _devices = devices;
        _metrics = metrics;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public void Enqueue(DeviceMapping mapping, string sourceValue, DateTimeOffset receivedAt)
    {
        if (_stop.IsCancellationRequested) return;
        var command = new GatewayCommand(mapping, sourceValue, receivedAt);
        if (mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase))
        {
            var resourceType = mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase)
                ? "grouped_light"
                : "light";
            var queueKey = $"hue|{mapping.TargetConnectionId}|{resourceType}";
            var targetKey = $"{resourceType}|{mapping.TargetId}";
            var pacing = resourceType == "grouped_light" ? HueGroupedLightInterval : HueLightInterval;
            GetQueue(queueKey, pacing, "hue", resourceType).Enqueue(targetKey, command);
            return;
        }

        // Tuya uses one independent serialized LAN command queue. DP values to the same device
        // coalesce, while Hue work can proceed concurrently without waiting for Tuya networking.
        GetQueue("tuya", TimeSpan.Zero, "tuya", "device").Enqueue($"{mapping.TargetConnectionId}|{mapping.TuyaDp}", command);
    }

    private LatestValueCommandQueue<GatewayCommand> GetQueue(string key, TimeSpan pacing, string provider, string resourceType)
    {
        lock (_queueSync)
        {
            if (_queues.TryGetValue(key, out var existing)) return existing;
            var created = new LatestValueCommandQueue<GatewayCommand>(
                (command, cancellationToken) => SendAsync(command, cancellationToken),
                pacing,
                _stop.Token,
                key,
                metrics: _metrics,
                provider: provider,
                resourceType: resourceType);
            _queues.Add(key, created);
            return created;
        }
    }

    private async Task SendAsync(GatewayCommand command, CancellationToken cancellationToken)
    {
        var mapping = command.Mapping;
        try
        {
            var settings = _store.GetSnapshot().Settings;
            var convertedValue = MappingValueConverter.Convert(mapping, command.SourceValue);
            Log.Information("Mapping {mapping} converted source value {SourceValue} to JSON {JsonType} {ConvertedValue}",
                mapping.Name, command.SourceValue, convertedValue.ValueKind, convertedValue.GetRawText());

            if (mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase))
            {
                var resourceType = mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase) ? "grouped_light" : "light";
                _metrics.RecordEventToDispatch("hue", resourceType, (DateTimeOffset.UtcNow - command.ReceivedAt).TotalMilliseconds);
                var bridge = settings.HueBridges.First(x => x.Id == mapping.TargetConnectionId);
                var applied = await _devices.SendHueAsync(bridge, mapping, convertedValue, cancellationToken, command.ReceivedAt);
                if (!applied)
                {
                    _metrics.RecordCommand("hue", resourceType, "invalid");
                    Log.Warning("Skipping Loxone value {Value} for mapping {Mapping} to Hue resource {TargetId}; converted value failed Hue validation",
                        command.SourceValue, mapping.Name, mapping.TargetId);
                    return;
                }

                _metrics.RecordCommand("hue", resourceType, "success");
                _store.ReportHueStatus(bridge.Id, "Connected; command accepted");
                Log.Information("Mapping {Mapping} command accepted by Hue resource {TargetId}; event-to-acceptance delay {EventToAcceptanceMs:F1} ms",
                    mapping.Name, mapping.TargetId, (DateTimeOffset.UtcNow - command.ReceivedAt).TotalMilliseconds);
            }
            else
            {
                _metrics.RecordEventToDispatch("tuya", "device", (DateTimeOffset.UtcNow - command.ReceivedAt).TotalMilliseconds);
                var device = settings.TuyaDevices.First(x => x.Id == mapping.TargetConnectionId);
                await _devices.SendTuyaAsync(device, mapping.TuyaDp!.Value, convertedValue, cancellationToken);
                _metrics.RecordCommand("tuya", "device", "success");
                _store.ReportTuyaStatus(device.Id, "Connected; last command dispatched (acknowledgement best-effort)");
                Log.Information("Mapping {Mapping} Tuya command dispatched to device {Device} DP {Dp}; acknowledgement is best-effort; event-to-dispatch delay {EventToDispatchMs:F1} ms",
                    mapping.Name, device.Name, mapping.TuyaDp, (DateTimeOffset.UtcNow - command.ReceivedAt).TotalMilliseconds);
            }

            _store.ReportSync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _metrics.RecordCommand(mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase) ? "hue" : "tuya",
                mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase)
                    ? (mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase) ? "grouped_light" : "light")
                    : "device", "failure");
            if (mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase))
                _store.ReportHueStatus(mapping.TargetConnectionId, $"Command pending: {ex.Message}");
            else
                _store.ReportTuyaStatus(mapping.TargetConnectionId, $"Command pending: {ex.Message}");
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        LatestValueCommandQueue<GatewayCommand>[] queues;
        lock (_queueSync) queues = [.. _queues.Values];
        foreach (var queue in queues)
            await queue.DisposeAsync();
        _stop.Dispose();
    }
}
