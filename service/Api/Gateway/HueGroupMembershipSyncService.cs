using Serilog;

namespace loxone.smart.gateway.Api.Gateway;

public sealed class HueGroupMembershipSyncService(GatewayStateStore store, DeviceIntegrations devices) : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RefreshMappedBridgesAsync(stoppingToken);
            try { await Task.Delay(SyncInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        }
    }

    private async Task RefreshMappedBridgesAsync(CancellationToken cancellationToken)
    {
        var settings = store.GetSnapshot().Settings;
        var mappedBridgeIds = settings.Mappings
            .Where(mapping => mapping.TargetType.Equals("hue", StringComparison.OrdinalIgnoreCase) &&
                              mapping.HueResourceType.Equals("grouped_light", StringComparison.OrdinalIgnoreCase))
            .Select(mapping => mapping.TargetConnectionId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var bridge in settings.HueBridges.Where(bridge => mappedBridgeIds.Contains(bridge.Id)))
        {
            try
            {
                var resources = await devices.GetHueResourcesAsync(bridge, cancellationToken);
                var catalog = await store.RefreshHueResourceCatalogAsync(bridge.Id, resources, cancellationToken);
                if (catalog is null) continue;

                var groupedLightCount = catalog.Resources.Count(resource => resource.Type == "grouped_light");
                var memberships = catalog.Resources.Count(resource => resource.Type == "grouped_light" && resource.MemberIds is { Count: > 0 });
                Log.Information("Refreshed Hue group membership for bridge {Bridge}; {GroupedLightCount} groups discovered and {MembershipCount} have exact light membership", bridge.Name, groupedLightCount, memberships);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                Log.Warning(exception, "Daily Hue group membership refresh failed for bridge {Bridge}; the last discovered catalog remains active", bridge.Name);
            }
        }
    }
}
