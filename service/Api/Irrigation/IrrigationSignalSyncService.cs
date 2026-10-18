using loxone.smart.gateway.Api.Gateway;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

/// <summary>Mirrors the irrigation recommendation to the configured Loxone digital input.</summary>
public sealed class IrrigationSignalSyncService(
    GatewayStateStore store,
    IrrigationService irrigation,
    LawnDryService lawnDryService,
    LoxoneIntegration loxone) : BackgroundService
{
    private readonly Dictionary<string, string> _lastApplied = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await SyncOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Warning(ex, "Unable to synchronize irrigation status signals to Loxone; will retry"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncOnceAsync(CancellationToken cancellationToken)
    {
        var settings = store.GetSnapshot().Settings;
        var configuration = settings.Irrigation;
        if (configuration.IrrigationRequiredTarget is null && configuration.LawnDryTarget is null) return;

        var irrigationDecision = configuration.IrrigationRequiredTarget is null
            ? null
            : await irrigation.CalculateAsync(cancellationToken);
        var lawnDryDecision = configuration.LawnDryTarget is null
            ? null
            : await lawnDryService.GetStatusAsync(cancellationToken);

        await SyncSignalAsync(settings, "Irrigation Required", "IrrigationRequired",
            configuration.IrrigationRequiredTarget,
            irrigationDecision is not null && irrigationDecision.LocalDataComplete && irrigationDecision.Irrigate,
            cancellationToken);
        await SyncSignalAsync(settings, "Lawn Dry", "LawnDry",
            configuration.LawnDryTarget,
            lawnDryDecision?.LawnDry == true,
            cancellationToken);
    }

    private async Task SyncSignalAsync(GatewaySettings settings, string name, string cacheKey,
        IrrigationLoxoneSignalTarget? target, bool value, CancellationToken cancellationToken)
    {
        if (target is null) return;
        if (string.IsNullOrWhiteSpace(target.ServerId) || string.IsNullOrWhiteSpace(target.ControlUuid))
        {
            Log.Warning("Cannot synchronize {Signal}: select a Miniserver and writable control in Irrigation configuration", name);
            return;
        }

        var server = settings.LoxoneServers.FirstOrDefault(x => x.Id.Equals(target.ServerId, StringComparison.OrdinalIgnoreCase));
        if (server is null)
        {
            Log.Warning("Cannot synchronize {Signal}: Miniserver {ServerId} is no longer configured", name, target.ServerId);
            return;
        }

        var signature = $"{target.ServerId}|{target.ControlUuid}|{value}";
        if (_lastApplied.TryGetValue(cacheKey, out var previous) && previous.Equals(signature, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            await loxone.SetDigitalInputAsync(server, target.ControlUuid, value, cancellationToken);
            _lastApplied[cacheKey] = signature;
            Log.Information("Synchronized Loxone signal {Signal}={Value} to {Control} ({StateUuid})", name, value, target.Control, target.StateUuid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log.Warning(ex, "Unable to synchronize Loxone signal {Signal}; will retry", name); }
    }
}
