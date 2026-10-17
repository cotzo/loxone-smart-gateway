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
                await store.WaitForChangeAsync(version, TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (TimeoutException) { }
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
        while (!cancellationToken.IsCancellationRequested)
        {
            var channel = Channel.CreateUnbounded<(string Uuid, string Value)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            using var processorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var processor = ProcessStatesAsync(server.Id, channel.Reader, processorCancellation.Token);
            try
            {
                store.ReportLoxoneStatus(server.Id, "Connecting");
                var watchedStateUuids = store.GetSnapshot().Settings.Mappings
                    .Where(mapping => mapping.LoxoneServerId.Equals(server.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(mapping => NormalizeUuid(mapping.LoxoneStateUuid))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                await loxone.RunSessionAsync(server,
                    (uuid, value) =>
                    {
                        Log.Information("Received mapped Loxone state {uuid} from {server} with value {value}", uuid, server.Name, value);
                        channel.Writer.TryWrite((uuid, value));
                    },
                    status => store.ReportLoxoneStatus(server.Id, status), watchedStateUuids, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                store.ReportLoxoneStatus(server.Id, $"Reconnecting: {ex.Message}");
                Log.Warning(ex, "Loxone connection {server} failed; retrying", server.Name);
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

    private async Task ProcessStatesAsync(string serverId, ChannelReader<(string Uuid, string Value)> reader, CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, (DeviceMapping Mapping, string Value, DateTimeOffset RetryAfter, int Attempts)>();
        var lastValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                if (lastValues.TryGetValue(state.Uuid, out var previous) && previous.Equals(state.Value))
                    continue;
                lastValues[state.Uuid] = state.Value;
                var settings = store.GetSnapshot().Settings;
                foreach (var mapping in settings.Mappings.Where(x =>
                             x.LoxoneServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase) &&
                             NormalizeUuid(x.LoxoneStateUuid).Equals(NormalizeUuid(state.Uuid), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Information("Loxone state {uuid} matched mapping {mapping}; queuing target update", state.Uuid, mapping.Name);
                    pending[mapping.Id] = (mapping, state.Value, DateTimeOffset.MinValue, 0);
                }
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var (id, item) in pending.ToArray())
            {
                if (item.RetryAfter > now) continue;
                try
                {
                    await ApplyMappingAsync(item.Mapping, item.Value, cancellationToken);
                    pending.Remove(id);
                    store.ReportSync();
                    Log.Information("Mapping {mapping} command accepted by {targetType} target {targetId}", item.Mapping.Name, item.Mapping.TargetType, item.Mapping.TargetId);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var attempts = item.Attempts + 1;
                    var backoffSeconds = Math.Min(60, Math.Pow(2, Math.Min(attempts, 6)));
                    pending[id] = (item.Mapping, item.Value, now.AddSeconds(backoffSeconds), attempts);
                    Log.Warning(ex, "Unable to apply Loxone state to mapping {mapping}; will retry", item.Mapping.Name);
                    if (item.Mapping.TargetType == "hue") store.ReportHueStatus(item.Mapping.TargetConnectionId, $"Command pending: {ex.Message}");
                    else store.ReportTuyaStatus(item.Mapping.TargetConnectionId, $"Command pending: {ex.Message}");
                }
            }
        }
    }

    private async Task ApplyMappingAsync(DeviceMapping mapping, string sourceValue, CancellationToken cancellationToken)
    {
        var settings = store.GetSnapshot().Settings;
        var convertedValue = MappingValueConverter.Convert(mapping, sourceValue);
        if (mapping.TargetType == "hue")
        {
            var bridge = settings.HueBridges.First(x => x.Id == mapping.TargetConnectionId);
            await devices.SendHueAsync(bridge, mapping, convertedValue, cancellationToken);
            store.ReportHueStatus(bridge.Id, "Connected; command accepted");
        }
        else
        {
            var device = settings.TuyaDevices.First(x => x.Id == mapping.TargetConnectionId);
            await devices.SendTuyaAsync(device, mapping.TuyaDp!.Value, convertedValue, cancellationToken);
            store.ReportTuyaStatus(device.Id, "Connected; command accepted");
        }
    }

    private static string NormalizeUuid(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);
}
