using loxone.smart.gateway.Api.PhilipsHue;
using System.Text.Json;
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
    // Loxone publishes a snapshot as separate state events. Give related grouped-light
    // updates a brief window to arrive before evaluating exact-coverage optimizations.
    private static readonly TimeSpan HueGroupedLightBatchWindow = TimeSpan.FromMilliseconds(200);
    private readonly GatewayStateStore _store;
    private readonly DeviceIntegrations _devices;
    private readonly GatewayMetrics _metrics;
    private readonly CancellationTokenSource _stop;
    private readonly object _queueSync = new();
    private readonly Dictionary<string, LatestValueCommandQueue<GatewayCommand>> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<GatewayCommand>> _groupedLightBatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scheduledGroupedLightBatches = new(StringComparer.OrdinalIgnoreCase);

    public GatewayCommandDispatcher(GatewayStateStore store, DeviceIntegrations devices, GatewayMetrics metrics, CancellationToken cancellationToken)
    {
        _store = store;
        _devices = devices;
        _metrics = metrics;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public void Enqueue(DeviceMapping mapping, string sourceValue, DateTimeOffset receivedAt)
        => EnqueueBatch([mapping], sourceValue, receivedAt);

    public void EnqueueBatch(IReadOnlyList<DeviceMapping> mappings, string sourceValue, DateTimeOffset receivedAt)
    {
        if (_stop.IsCancellationRequested) return;
        if (mappings.Count == 0) return;
        var snapshot = _store.GetSnapshot().Settings;
        var hueMappings = mappings.Where(mapping => mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase)).ToArray();
        var groupedLightCommands = new List<GatewayCommand>();
        foreach (var mapping in hueMappings)
        {
            var command = new GatewayCommand(mapping, sourceValue, receivedAt);
            var resourceType = mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase) ? "grouped_light" : "light";
            if (resourceType == "grouped_light")
            {
                groupedLightCommands.Add(command);
                continue;
            }
            EnqueueHueCommand(command, resourceType, snapshot);
        }

        foreach (var bridgeBatch in groupedLightCommands.GroupBy(command => command.Mapping.TargetConnectionId, StringComparer.OrdinalIgnoreCase))
        {
            var bridgeId = bridgeBatch.Key;
            lock (_queueSync)
            {
                if (!_groupedLightBatches.TryGetValue(bridgeId, out var batch))
                    _groupedLightBatches[bridgeId] = batch = [];
                batch.AddRange(bridgeBatch);
                if (_scheduledGroupedLightBatches.Add(bridgeId))
                    _ = FlushGroupedLightBatchAfterDelayAsync(bridgeId);
            }
        }

        foreach (var mapping in mappings.Where(mapping => !mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase)))
        {
            var command = new GatewayCommand(mapping, sourceValue, receivedAt);
            // Tuya uses one independent serialized LAN command queue. DP values to the same device
            // coalesce, while Hue work can proceed concurrently without waiting for Tuya networking.
            GetQueue("tuya", TimeSpan.Zero, "tuya", "device").Enqueue($"{mapping.TargetConnectionId}|{mapping.TuyaDp}", command);
        }
    }

    private async Task FlushGroupedLightBatchAfterDelayAsync(string bridgeId)
    {
        try
        {
            await Task.Delay(HueGroupedLightBatchWindow, _stop.Token);
            List<GatewayCommand> batch;
            lock (_queueSync)
            {
                _scheduledGroupedLightBatches.Remove(bridgeId);
                if (!_groupedLightBatches.Remove(bridgeId, out batch!)) return;
            }

            // Keep only the latest value for a target inside the collection window.
            var latestByTarget = batch
                .GroupBy(command => command.Mapping.TargetId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToArray();
            Log.Debug("Collected {CommandCount} grouped-light updates across state events for Hue bridge {BridgeId}; evaluating group coverage",
                latestByTarget.Length, bridgeId);
            var settings = _store.GetSnapshot().Settings;
            foreach (var command in OptimizeHueBatch(latestByTarget, settings))
                EnqueueHueCommand(command, "grouped_light", settings);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void EnqueueHueCommand(GatewayCommand command, string resourceType, GatewaySettings snapshot)
    {
        var mapping = command.Mapping;
        var queueKey = $"hue|{mapping.TargetConnectionId}|{resourceType}";
        var targetKey = $"{resourceType}|{mapping.TargetId}";
        var pacing = resourceType == "grouped_light" ? HueGroupedLightInterval : HueLightInterval;
        var bridgeName = snapshot.HueBridges
            .FirstOrDefault(x => x.Id.Equals(mapping.TargetConnectionId, StringComparison.OrdinalIgnoreCase))?.Name;
        GetQueue(queueKey, pacing, "hue", resourceType, bridgeName).Enqueue(targetKey, command);
    }

    private IReadOnlyList<GatewayCommand> OptimizeHueBatch(IReadOnlyList<GatewayCommand> commands, GatewaySettings settings)
    {
        if (commands.Count < 2) return commands;

        var prepared = new List<(GatewayCommand Command, string Body, HashSet<string> Members)>();
        foreach (var command in commands)
        {
            var mapping = command.Mapping;
            if (!mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var converted = JsonDocument.Parse(MappingValueConverter.Convert(mapping, command.SourceValue).GetRawText());
                if (!DeviceIntegrations.TryBuildHueCommandBody(mapping, converted.RootElement, out var body))
                {
                    Log.Debug("Cannot optimize Hue mapping {Mapping}: converted value is not valid for command type {CommandType}",
                        mapping.Name, mapping.HueLightType);
                    continue;
                }
                var catalog = settings.HueResourceCatalogs.GetValueOrDefault(mapping.TargetConnectionId);
                var resource = catalog?.Resources.FirstOrDefault(resource => resource.Type == "grouped_light" && resource.Id.Equals(mapping.TargetId, StringComparison.OrdinalIgnoreCase));
                if (resource?.MemberIds is not { Count: > 0 })
                {
                    Log.Debug("Cannot optimize Hue mapping {Mapping}: grouped-light {TargetId} has no discovered member-light list",
                        mapping.Name, mapping.TargetId);
                    continue;
                }
                prepared.Add((command, body, new HashSet<string>(resource.MemberIds, StringComparer.OrdinalIgnoreCase)));
                Log.Debug("Hue optimization input {Mapping}: command {CommandBody}; member lights [{MemberIds}]",
                    mapping.Name, body, string.Join(",", resource.MemberIds));
            }
            catch (Exception ex)
            {
                // Preserve the normal queued validation and retry path for values that cannot be prepared.
                Log.Debug(ex, "Cannot prepare Hue mapping {Mapping} for grouped-light optimization", mapping.Name);
            }
        }

        if (prepared.Count < 2)
        {
            Log.Debug("Hue grouped-light batch is not eligible for combination: {EligibleCount} of {CommandCount} updates have valid commands and discovered member-light lists",
                prepared.Count, commands.Count);
            return commands;
        }
        var used = new HashSet<GatewayCommand>();
        var replacements = new Dictionary<GatewayCommand, GatewayCommand>();
        foreach (var bridgeGroup in prepared.GroupBy(item => item.Command.Mapping.TargetConnectionId, StringComparer.OrdinalIgnoreCase))
        {
            var catalog = settings.HueResourceCatalogs.GetValueOrDefault(bridgeGroup.Key);
            if (catalog is null) continue;
            foreach (var candidate in catalog.Resources
                         .Where(resource => resource.Type == "grouped_light" && resource.MemberIds is { Count: > 0 })
                         .OrderByDescending(resource => resource.MemberIds!.Count))
            {
                var candidateMembers = new HashSet<string>(candidate.MemberIds!, StringComparer.OrdinalIgnoreCase);
                var relevant = bridgeGroup.Where(item => !used.Contains(item.Command) &&
                    !item.Command.Mapping.TargetId.Equals(candidate.Id, StringComparison.OrdinalIgnoreCase) &&
                    item.Members.Overlaps(candidateMembers)).ToArray();
                if (relevant.Length < 2) continue;
                var outsideCandidate = relevant.Where(item => !item.Members.IsSubsetOf(candidateMembers)).ToArray();
                if (outsideCandidate.Length > 0)
                {
                    Log.Debug("Hue group candidate {CandidateName} ({CandidateId}) rejected: mapped group(s) {Mappings} include lights outside the candidate room/zone",
                        candidate.Name, candidate.Id, string.Join(", ", outsideCandidate.Select(item => item.Command.Mapping.Name)));
                    continue;
                }
                if (relevant.Select(item => item.Body).Distinct(StringComparer.Ordinal).Skip(1).Any())
                {
                    Log.Debug("Hue group candidate {CandidateName} ({CandidateId}) rejected: mapped commands differ ({Commands})",
                        candidate.Name, candidate.Id, string.Join(" | ", relevant.Select(item => $"{item.Command.Mapping.Name}={item.Body}")));
                    continue;
                }
                var covered = new HashSet<string>(relevant.SelectMany(item => item.Members), StringComparer.OrdinalIgnoreCase);
                if (!covered.SetEquals(candidateMembers))
                {
                    Log.Debug("Hue group candidate {CandidateName} ({CandidateId}) rejected: exact light coverage differs; mapped union [{MappedLightIds}], candidate lights [{CandidateLightIds}]",
                        candidate.Name, candidate.Id, string.Join(",", covered), string.Join(",", candidateMembers));
                    continue;
                }

                var representative = relevant[0].Command;
                var aggregateMapping = new DeviceMapping
                {
                    Id = representative.Mapping.Id,
                    Name = $"{representative.Mapping.Name} (Hue group optimization)",
                    TargetType = "hue",
                    TargetConnectionId = representative.Mapping.TargetConnectionId,
                    TargetId = candidate.Id,
                    HueResourceType = "grouped_light",
                    HueLightType = representative.Mapping.HueLightType,
                    TransitionTime = representative.Mapping.TransitionTime,
                    Converter = representative.Mapping.Converter
                };
                foreach (var item in relevant)
                {
                    used.Add(item.Command);
                    replacements[item.Command] = new GatewayCommand(aggregateMapping, item.Command.SourceValue, item.Command.ReceivedAt);
                }
                Log.Information("Combining {MappingCount} Hue grouped-light updates into one request to group {GroupId} because the commands match and coverage is exact",
                    relevant.Length, candidate.Id);
                var bridgeName = settings.HueBridges.FirstOrDefault(bridge => bridge.Id.Equals(bridgeGroup.Key, StringComparison.OrdinalIgnoreCase))?.Name;
                _metrics.RecordHueGroupOptimization(relevant.Length, bridgeName);
            }
        }

        if (replacements.Count == 0)
            Log.Debug("No exact Hue room/zone grouped-light resource covers the collected mapped light sets with matching commands");

        var output = new List<GatewayCommand>();
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            if (!replacements.TryGetValue(command, out var replacement))
            {
                output.Add(command);
                continue;
            }
            var key = $"{replacement.Mapping.TargetConnectionId}|{replacement.Mapping.TargetId}|{replacement.SourceValue}|{replacement.Mapping.HueLightType}|{replacement.Mapping.TransitionTime}";
            if (emitted.Add(key)) output.Add(replacement);
        }
        return output;
    }

    private LatestValueCommandQueue<GatewayCommand> GetQueue(string key, TimeSpan pacing, string provider, string resourceType, string? bridge = null)
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
                resourceType: resourceType,
                bridge: bridge);
            _queues.Add(key, created);
            return created;
        }
    }

    private async Task SendAsync(GatewayCommand command, CancellationToken cancellationToken)
    {
        var mapping = command.Mapping;
        var settings = _store.GetSnapshot().Settings;
        var bridge = mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase)
            ? settings.HueBridges.FirstOrDefault(x => x.Id == mapping.TargetConnectionId)
            : null;
        var isHue = mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase);
        var provider = isHue ? "hue" : "tuya";
        var resourceType = isHue
            ? (mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase) ? "grouped_light" : "light")
            : "device";
        var bridgeName = bridge?.Name;
        try
        {
            var convertedValue = MappingValueConverter.Convert(mapping, command.SourceValue);
            Log.Information("Mapping {mapping} converted source value {SourceValue} to JSON {JsonType} {ConvertedValue}",
                mapping.Name, command.SourceValue, convertedValue.ValueKind, convertedValue.GetRawText());

            if (mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase))
            {
                var selectedBridge = bridge ?? throw new InvalidOperationException($"Hue bridge {mapping.TargetConnectionId} is not configured.");
                _metrics.RecordEventToDispatch("hue", resourceType, (DateTimeOffset.UtcNow - command.ReceivedAt).TotalMilliseconds, bridgeName);
                var applied = await _devices.SendHueAsync(selectedBridge, mapping, convertedValue, cancellationToken, command.ReceivedAt);
                if (!applied)
                {
                    _metrics.RecordCommand("hue", resourceType, "invalid", bridgeName);
                    Log.Warning("Skipping Loxone value {Value} for mapping {Mapping} to Hue resource {TargetId}; converted value failed Hue validation",
                        command.SourceValue, mapping.Name, mapping.TargetId);
                    return;
                }

                _metrics.RecordCommand("hue", resourceType, "success", bridgeName);
                _store.ReportHueStatus(selectedBridge.Id, "Connected; command accepted");
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
            _metrics.RecordCommand(provider, resourceType, "failure", bridgeName);
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
