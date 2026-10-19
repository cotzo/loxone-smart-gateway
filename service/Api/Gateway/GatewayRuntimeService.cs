using System.Globalization;
using Loxone.Communicator;
using Serilog;
using System.Threading.Channels;

namespace loxone.smart.gateway.Api.Gateway;

public sealed class GatewayRuntimeService(
    GatewayStateStore store,
    LoxoneIntegration loxone,
    DeviceIntegrations devices,
    GatewayMetrics metrics,
    loxone.smart.gateway.Api.Irrigation.IrrigationLoxoneService irrigationLoxone,
    loxone.smart.gateway.Api.Irrigation.IrrigationWeatherStateService irrigationWeather) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var (settings, version) = store.GetSnapshot();
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            await using var dispatcher = new GatewayCommandDispatcher(store, devices, metrics, generation.Token);
            var workers = settings.LoxoneServers.Select(server => RunServerLoopAsync(server, dispatcher, generation.Token)).ToArray();
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

    private async Task RunServerLoopAsync(LoxoneSettings server, GatewayCommandDispatcher dispatcher, CancellationToken cancellationToken)
    {
        // Keep source-value deduplication across websocket reconnects. The dispatcher independently
        // retains queued/retry state for the lifetime of this settings generation.
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
            var processor = ProcessStatesAsync(server.Id, channel.Reader, dispatcher, lastValues, processorCancellation.Token);
            try
            {
                Log.Information("Starting Loxone session {SessionAttempt} for {server}", sessionAttempt, server.Name);
                store.ReportLoxoneStatus(server.Id, "Connecting");
                var watchedStateUuids = store.GetSnapshot().Settings.Mappings
                    .Where(mapping => mapping.LoxoneServerId.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(mapping => NormalizeUuid(mapping.LoxoneStateUuid))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var irrigationBlock = store.GetSnapshot().Settings.Irrigation.LoxoneBlock;
                foreach (var mapping in store.GetSnapshot().Settings.Irrigation.WeatherStateMappings.Where(x => x.ServerId.Equals(server.Id, StringComparison.OrdinalIgnoreCase)))
                    watchedStateUuids.Add(NormalizeUuid(mapping.StateUuid));
                if (irrigationBlock is not null && irrigationBlock.ServerId.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
                {
                    watchedStateUuids.Add(NormalizeUuid(irrigationBlock.ActiveStateUuid));
                    watchedStateUuids.Add(NormalizeUuid(irrigationBlock.CurrentZoneStateUuid));
                }
                await loxone.RunSessionAsync(server,
                    (uuid, value) =>
                    {
                        var receivedAt = DateTimeOffset.UtcNow;
                        Log.Debug("Received mapped Loxone state {uuid} from {server} with value {value} in session {SessionAttempt}", uuid, server.Name, value, sessionAttempt);
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
        GatewayCommandDispatcher dispatcher, Dictionary<string, string> lastValues, CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (reader.TryRead(out var state))
            {
                var normalizedUuid = NormalizeUuid(state.Uuid);
                if (lastValues.TryGetValue(normalizedUuid, out var previous) && previous.Equals(state.Value))
                {
                    Log.Debug("Ignoring repeated Loxone state {uuid} with unchanged value {value}; reconnect snapshot will not be replayed", state.Uuid, state.Value);
                    continue;
                }
                lastValues[normalizedUuid] = state.Value;
                try { await irrigationLoxone.ObserveStateAsync(serverId, state.Uuid, state.Value, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warning(ex, "Unable to process irrigation currentZone state {Uuid} from Miniserver {ServerId}", state.Uuid, serverId); }
                try { await irrigationWeather.ObserveStateAsync(serverId, state.Uuid, state.Value, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { Log.Warning(ex, "Unable to process mapped irrigation weather state {Uuid} from Miniserver {ServerId}", state.Uuid, serverId); }
                var settings = store.GetSnapshot().Settings;
                var matchedMappings = settings.Mappings.Where(x =>
                             x.LoxoneServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase) &&
                             NormalizeUuid(x.LoxoneStateUuid).Equals(NormalizeUuid(state.Uuid), StringComparison.OrdinalIgnoreCase)).ToArray();
                foreach (var mapping in matchedMappings)
                    Log.Information("Loxone state {uuid} matched mapping {mapping}; queuing target update after {EventToQueueMs:F1} ms", state.Uuid, mapping.Name, (DateTimeOffset.UtcNow - state.ReceivedAt).TotalMilliseconds);
                dispatcher.EnqueueBatch(matchedMappings, state.Value, state.ReceivedAt);
            }
        }
    }

    private static string NormalizeUuid(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);
}

