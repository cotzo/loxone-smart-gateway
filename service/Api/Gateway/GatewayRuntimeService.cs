using System.Globalization;
using Loxone.Communicator;
using Serilog;
using System.Threading.Channels;

namespace loxone.smart.gateway.Api.Gateway;

public sealed class GatewayRuntimeService(
    GatewayStateStore store,
    LoxoneIntegration loxone,
    DeviceIntegrations devices) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var (settings, version) = store.GetSnapshot();
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var workers = settings.LoxoneServers.Select(server => RunServerLoopAsync(server, generation.Token)).ToArray();
            try
            {
                // Settings changes advance the version and restart workers. A periodic timeout
                // would needlessly tear down healthy sessions and replay Loxone's full snapshot.
                await store.WaitForChangeAsync(version, Timeout.InfiniteTimeSpan, stoppingToken);
            }
            finally
            {
                generation.Cancel();
                try { await Task.WhenAll(workers); }
                catch (OperationCanceledException) { }
            }
        }
    }

    private async Task RunServerLoopAsync(LoxoneSettings server, CancellationToken cancellationToken)
    {
        // Keep both deduplication and retry state across websocket reconnects. Loxone sends
        // a full snapshot after reconnect; resetting these would replay every target command.
        var pending = new Dictionary<string, (DeviceMapping Mapping, string Value, DateTimeOffset ReceivedAt, DateTimeOffset RetryAfter, int Attempts)>();
        var lastValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sessionAttempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            sessionAttempt++;
            var channel = Channel.CreateUnbounded<(string Uuid, string Value, DateTimeOffset ReceivedAt)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            using var processorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var processor = ProcessStatesAsync(server.Id, channel.Reader, pending, lastValues, processorCancellation.Token);
            try
            {
                Log.Information("Starting Loxone session {SessionAttempt} for {server}", sessionAttempt, server.Name);
                store.ReportLoxoneStatus(server.Id, "Connecting");
                var watchedStateUuids = store.GetSnapshot().Settings.Mappings
                    .Where(mapping => mapping.LoxoneServerId.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(mapping => NormalizeUuid(mapping.LoxoneStateUuid))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                await loxone.RunSessionAsync(server,
                    (uuid, value) =>
                    {
                        var receivedAt = DateTimeOffset.UtcNow;
                        Log.Information("Received mapped Loxone state {uuid} from {server} with value {value} in session {SessionAttempt}", uuid, server.Name, value, sessionAttempt);
                        if (!channel.Writer.TryWrite((uuid, value, receivedAt)))
                            Log.Warning("Could not queue Loxone state {uuid} from {server} in session {SessionAttempt}; the session processor is no longer accepting events", uuid, server.Name, sessionAttempt);
                    },
                    status => store.ReportLoxoneStatus(server.Id, status), watchedStateUuids, cancellationToken, sessionAttempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                store.ReportLoxoneStatus(server.Id, $"Reconnecting: {ex.Message}");
                Log.Warning(ex, "Loxone connection {server} session {SessionAttempt} failed; retrying", server.Name, sessionAttempt);
            }
            finally
            {
                processorCancellation.Cancel();
                channel.Writer.TryComplete();
                try { await processor; }
                catch (OperationCanceledException) when (processorCancellation.IsCancellationRequested) { }
            }

            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ProcessStatesAsync(string serverId, ChannelReader<(string Uuid, string Value, DateTimeOffset ReceivedAt)> reader,
        Dictionary<string, (DeviceMapping Mapping, string Value, DateTimeOffset ReceivedAt, DateTimeOffset RetryAfter, int Attempts)> pending,
        Dictionary<string, string> lastValues, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (!cancellationToken.IsCancellationRequested)
        {
            using var iteration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var readTask = reader.WaitToReadAsync(iteration.Token).AsTask();
            var tickTask = timer.WaitForNextTickAsync(iteration.Token).AsTask();
            var winner = await Task.WhenAny(readTask, tickTask);
            if (winner == readTask && !readTask.Result) break;
            iteration.Cancel();
            try { await Task.WhenAll(readTask, tickTask); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }

            while (reader.TryRead(out var state))
            {
                var normalizedUuid = NormalizeUuid(state.Uuid);
                if (lastValues.TryGetValue(normalizedUuid, out var previous) && previous.Equals(state.Value))
                {
                    Log.Debug("Ignoring repeated Loxone state {uuid} with unchanged value {value}; reconnect snapshot will not be replayed", state.Uuid, state.Value);
                    continue;
                }
                lastValues[normalizedUuid] = state.Value;
                var settings = store.GetSnapshot().Settings;
                foreach (var mapping in settings.Mappings.Where(x =>
                             x.LoxoneServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase) &&
                             NormalizeUuid(x.LoxoneStateUuid).Equals(NormalizeUuid(state.Uuid), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Information("Loxone state {uuid} matched mapping {mapping}; queuing target update after {EventToQueueMs:F1} ms", state.Uuid, mapping.Name, (DateTimeOffset.UtcNow - state.ReceivedAt).TotalMilliseconds);
                    pending[mapping.Id] = (mapping, state.Value, state.ReceivedAt, DateTimeOffset.MinValue, 0);
                }
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var (id, item) in pending.ToArray())
            {
                if (item.RetryAfter > now) continue;
                try
                {
                    var applied = await ApplyMappingAsync(item.Mapping, item.Value, item.ReceivedAt, cancellationToken);
                    pending.Remove(id);
                    if (applied)
                    {
                        store.ReportSync();
                        if (item.Mapping.TargetType == "hue")
                            Log.Information("Mapping {mapping} command accepted by Hue resource {targetId}; event-to-acceptance delay {EventToAcceptanceMs:F1} ms", item.Mapping.Name, item.Mapping.TargetId, (DateTimeOffset.UtcNow - item.ReceivedAt).TotalMilliseconds);
                        else
                            Log.Information("Mapping {mapping} Tuya command dispatched to device {device} DP {dp}; acknowledgement is best-effort; event-to-dispatch delay {EventToDispatchMs:F1} ms", item.Mapping.Name, store.GetSnapshot().Settings.TuyaDevices.FirstOrDefault(x => x.Id == item.Mapping.TargetConnectionId)?.Name ?? item.Mapping.TargetConnectionId, item.Mapping.TuyaDp, (DateTimeOffset.UtcNow - item.ReceivedAt).TotalMilliseconds);
                    }
                    else
                    {
                        Log.Warning("Skipping Loxone value {value} for mapping {mapping} from source {SourceUuid} to Hue resource {TargetId}; converted value failed Hue tunable-white validation", item.Value, item.Mapping.Name, item.Mapping.LoxoneStateUuid, item.Mapping.TargetId);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var attempts = item.Attempts + 1;
                    var backoffSeconds = Math.Min(60, Math.Pow(2, Math.Min(attempts, 6)));
                    pending[id] = (item.Mapping, item.Value, item.ReceivedAt, now.AddSeconds(backoffSeconds), attempts);
                    Log.Warning(ex, "Unable to apply Loxone value {value} from source {SourceUuid} to mapping {mapping} on attempt {Attempt}; retrying in {RetryDelaySeconds:F0} s after {EventToFailureMs:F1} ms", item.Value, item.Mapping.LoxoneStateUuid, item.Mapping.Name, attempts, backoffSeconds, (DateTimeOffset.UtcNow - item.ReceivedAt).TotalMilliseconds);
                    if (item.Mapping.TargetType == "hue") store.ReportHueStatus(item.Mapping.TargetConnectionId, $"Command pending: {ex.Message}");
                    else store.ReportTuyaStatus(item.Mapping.TargetConnectionId, $"Command pending: {ex.Message}");
                }
            }
        }
    }

    private async Task<bool> ApplyMappingAsync(DeviceMapping mapping, string sourceValue, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var settings = store.GetSnapshot().Settings;
        var convertedValue = MappingValueConverter.Convert(mapping, sourceValue);
        Log.Information("Mapping {mapping} converted source value {SourceValue} to JSON {JsonType} {ConvertedValue}", mapping.Name, sourceValue, convertedValue.ValueKind, convertedValue.GetRawText());
        if (mapping.TargetType == "hue")
        {
            var bridge = settings.HueBridges.First(x => x.Id == mapping.TargetConnectionId);
            var applied = await devices.SendHueAsync(bridge, mapping, convertedValue, cancellationToken, receivedAt);
            if (!applied) return false;
            store.ReportHueStatus(bridge.Id, "Connected; command accepted");
        }
        else
        {
            var device = settings.TuyaDevices.First(x => x.Id == mapping.TargetConnectionId);
            await devices.SendTuyaAsync(device, mapping.TuyaDp!.Value, convertedValue, cancellationToken);
            store.ReportTuyaStatus(device.Id, "Connected; last command dispatched (acknowledgement best-effort)");
        }

        return true;
    }

    private static string NormalizeUuid(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);
}
