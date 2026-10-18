using loxone.smart.gateway.Api.Gateway;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

/// <summary>
/// Keeps the Irrigation function block's configured zone durations aligned with the
/// gateway's latest recommendations. It never starts or stops irrigation.
/// </summary>
public sealed class IrrigationDurationSyncService(
    GatewayStateStore store,
    IrrigationService irrigation,
    LoxoneIntegration loxone) : BackgroundService
{
    private const string OffValue = "-1";
    private string? _lastAppliedSignature;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Warning(ex, "Unable to synchronize irrigation durations to Loxone; will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        var settings = store.GetSnapshot().Settings;
        var block = settings.Irrigation.LoxoneBlock;
        if (block is null) return;
        if (string.IsNullOrWhiteSpace(block.ControlUuid))
        {
            Log.Warning("Cannot synchronize irrigation durations for {Control}: the imported block has no Loxone control UUID. Refresh Loxone resources and re-import the block", block.Control);
            return;
        }

        var zones = settings.Irrigation.Zones
            .Where(zone => zone.LoxoneBlockId.Equals(IrrigationLoxoneService.MakeBlockIdForSync(block.ServerId, block.Control), StringComparison.OrdinalIgnoreCase))
            .Where(zone => !string.IsNullOrWhiteSpace(zone.LoxoneZoneId))
            .ToList();
        if (zones.Count == 0) return;

        var decision = await irrigation.CalculateAsync(cancellationToken);
        var results = decision.Zones.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var durations = zones.ToDictionary(zone => zone.LoxoneZoneId,
            zone => decision.LocalDataComplete && results.TryGetValue(zone.Id, out var result) ? result.RuntimeSeconds : 0,
            StringComparer.OrdinalIgnoreCase);
        var signature = $"{block.ServerId}|{block.ControlUuid}|" +
            string.Join(";", durations.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"));
        if (signature == _lastAppliedSignature) return;

        var server = settings.LoxoneServers.FirstOrDefault(x => x.Id.Equals(block.ServerId, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            Log.Warning("Cannot synchronize irrigation durations: Miniserver {ServerId} is no longer configured", block.ServerId);
            return;
        }

        var stateValues = await loxone.ReadStatesOnceAsync(server,
            new HashSet<string>([block.ActiveStateUuid, block.CurrentZoneStateUuid], StringComparer.OrdinalIgnoreCase), cancellationToken);
        if (!stateValues.TryGetValue(LoxoneIntegration.NormalizeStateUuidForSync(block.ActiveStateUuid), out var activeRaw) ||
            !stateValues.TryGetValue(LoxoneIntegration.NormalizeStateUuidForSync(block.CurrentZoneStateUuid), out var currentRaw))
            throw new InvalidOperationException($"Could not read active/currentZone for irrigation block {block.Control}.");
        if (!IrrigationLoxoneService.IsInactiveForSync(activeRaw, currentRaw))
        {
            Log.Information("Deferring recommended irrigation duration update for {Control}: block is active (active={Active}, currentZone={CurrentZone})", block.Control, activeRaw, currentRaw);
            return;
        }

        await loxone.SetIrrigationDurationsAsync(server, block.ControlUuid, durations, cancellationToken);
        _lastAppliedSignature = signature;
        if (!decision.LocalDataComplete)
            Log.Warning("Weather coverage is incomplete; set all irrigation durations on {Control} to 0", block.Control);
        Log.Information("Synchronized recommended irrigation durations for {Control}: {Durations}", block.Control, signature);
    }
}
