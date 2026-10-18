using loxone.smart.gateway.Api.Gateway;
using loxone.smart.gateway.Api.Irrigation;
using Microsoft.AspNetCore.Mvc;

namespace loxone.smart.gateway.Controllers;

[ApiController]
[Route("api")]
public sealed class GatewayController(
    GatewayStateStore store,
    LoxoneIntegration loxone,
    DeviceIntegrations devices,
    IrrigationService irrigation,
    IrrigationRunTracker irrigationRunTracker,
    LawnDryService lawnDryService,
    IrrigationLoxoneService irrigationLoxone) : ControllerBase
{
    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request, [FromServices] IConfiguration configuration)
    {
        var expected = configuration["Gateway:AdminPassword"] ?? string.Empty;
        var expectedHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expected));
        var suppliedHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.Password ?? string.Empty));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash)
            ? Ok(new { token = expected })
            : Unauthorized();
    }

    [HttpGet("settings")]
    public ActionResult<SafeGatewaySettings> GetSettings()
    {
        var settings = store.GetSnapshot().Settings;
        return Ok(new SafeGatewaySettings(
            settings.LoxoneServers.Select(x => new SafeLoxoneSettings(x.Id, x.Name, x.Host, x.Port, x.UseTls, x.CertificateAuthorityPem, x.Username, !string.IsNullOrEmpty(x.Password))).ToList(),
            settings.LoxoneStateCatalogs,
            settings.HueBridges.Select(x => new SafeHueSettings(x.Id, x.Name, x.Host, !string.IsNullOrEmpty(x.ApplicationKey))).ToList(),
            settings.HueResourceCatalogs,
            settings.TuyaDevices.Select(x => new SafeTuyaDeviceSettings(x.Id, x.Name, x.DeviceId, x.IP, x.Version, !string.IsNullOrEmpty(x.LocalKey))).ToList(),
            settings.Mappings,
            settings.Irrigation));
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] GatewaySettings settings, CancellationToken cancellationToken)
    {
        var existing = store.GetSnapshot().Settings;
        settings.LoxoneStateCatalogs = new Dictionary<string, LoxoneStateCatalog>(StringComparer.OrdinalIgnoreCase);
        settings.HueResourceCatalogs = new Dictionary<string, HueResourceCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var server in settings.LoxoneServers)
        {
            if (string.IsNullOrEmpty(server.Password)) server.Password = existing.LoxoneServers.FirstOrDefault(x => x.Id == server.Id)?.Password ?? string.Empty;
            var previous = existing.LoxoneServers.FirstOrDefault(x => x.Id.Equals(server.Id, StringComparison.OrdinalIgnoreCase));
            if (previous is not null && previous.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase) && previous.Username.Equals(server.Username, StringComparison.Ordinal))
            {
                var catalog = existing.LoxoneStateCatalogs.FirstOrDefault(x => x.Key.Equals(server.Id, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(catalog.Key)) settings.LoxoneStateCatalogs[server.Id] = catalog.Value;
            }
        }
        foreach (var bridge in settings.HueBridges)
        {
            var previous = existing.HueBridges.FirstOrDefault(x => x.Id.Equals(bridge.Id, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(bridge.ApplicationKey)) bridge.ApplicationKey = previous?.ApplicationKey ?? string.Empty;
            if (previous is not null && previous.Host.Equals(bridge.Host, StringComparison.OrdinalIgnoreCase))
            {
                var catalog = existing.HueResourceCatalogs.FirstOrDefault(x => x.Key.Equals(bridge.Id, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(catalog.Key)) settings.HueResourceCatalogs[bridge.Id] = catalog.Value;
            }
        }
        foreach (var device in settings.TuyaDevices)
            if (string.IsNullOrEmpty(device.LocalKey)) device.LocalKey = existing.TuyaDevices.FirstOrDefault(x => x.Id == device.Id)?.LocalKey ?? string.Empty;

        try
        {
            await store.SaveAsync(settings, cancellationToken);
            irrigation.UpdateConfiguration(settings.Irrigation);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("status")]
    public ActionResult<IntegrationStatus> GetStatus() => Ok(store.GetStatus());

    [HttpPost("loxone/{serverId}/test")]
    public async Task<IActionResult> TestLoxone(string serverId, CancellationToken cancellationToken)
    {
        var server = Find(store.GetSnapshot().Settings.LoxoneServers, serverId);
        if (server is null) return NotFound();
        try
        {
            await loxone.CheckAsync(server, cancellationToken);
            return Ok(new { connected = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("loxone/{serverId}/resources")]
    public ActionResult<IReadOnlyList<LoxoneStateOption>> GetLoxoneResources(string serverId)
    {
        var settings = store.GetSnapshot().Settings;
        var server = Find(settings.LoxoneServers, serverId);
        if (server is null) return NotFound();
        var catalog = settings.LoxoneStateCatalogs.FirstOrDefault(x => x.Key.Equals(serverId, StringComparison.OrdinalIgnoreCase));
        return Ok(string.IsNullOrEmpty(catalog.Key) ? Array.Empty<LoxoneStateOption>() : catalog.Value.States);
    }

    [HttpPost("loxone/{serverId}/resources/refresh")]
    public async Task<ActionResult<LoxoneStateCatalog>> RefreshLoxoneResources(string serverId, CancellationToken cancellationToken)
    {
        var server = Find(store.GetSnapshot().Settings.LoxoneServers, serverId);
        if (server is null) return NotFound(new { error = "The selected Miniserver is not in the saved configuration. Save the connection settings, then refresh again." });
        try
        {
            var states = await loxone.DiscoverAsync(server, cancellationToken);
            return Ok(await store.SaveLoxoneStateCatalogAsync(serverId, states, cancellationToken));
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("hue/{bridgeId}/resources")]
    public async Task<ActionResult<IReadOnlyList<HueResource>>> GetHueResources(string bridgeId, CancellationToken cancellationToken)
    {
        var bridge = Find(store.GetSnapshot().Settings.HueBridges, bridgeId);
        if (bridge is null) return NotFound();
        try
        {
            var resources = await devices.GetHueResourcesAsync(bridge, cancellationToken);
            await store.SaveHueResourceCatalogAsync(bridgeId, resources, cancellationToken);
            return Ok(resources);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("tuya/{deviceId}/datapoints")]
    public async Task<ActionResult<IReadOnlyList<TuyaDataPoint>>> GetTuyaDataPoints(string deviceId, CancellationToken cancellationToken)
    {
        var device = Find(store.GetSnapshot().Settings.TuyaDevices, deviceId);
        if (device is null) return NotFound();
        try { return Ok(await devices.GetTuyaDataPointsAsync(device, cancellationToken)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("irrigation")]
    public ActionResult<IrrigationConfiguration> GetIrrigation() => Ok(store.GetSnapshot().Settings.Irrigation);

    [HttpGet("irrigation/loxone/blocks")]
    public ActionResult<IReadOnlyList<IrrigationBlockOption>> GetIrrigationBlocks() => Ok(irrigationLoxone.GetBlocks());

    [HttpPost("irrigation/loxone/preview")]
    public async Task<IActionResult> PreviewIrrigationBlock([FromBody] IrrigationBlockRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await irrigationLoxone.PreviewAsync(request.BlockId, cancellationToken)); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("irrigation/loxone/import")]
    public async Task<IActionResult> ImportIrrigationBlock([FromBody] IrrigationBlockRequest request, CancellationToken cancellationToken)
    {
        try { return Ok(await irrigationLoxone.ImportAsync(request.BlockId, cancellationToken)); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or TimeoutException or System.Text.Json.JsonException)
        { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("irrigation/status")]
    public async Task<ActionResult<IrrigationDashboardSnapshot>> GetIrrigationStatus(CancellationToken cancellationToken)
    {
        var decision = await irrigation.CalculateAsync(cancellationToken);
        var stored = await irrigation.GetStoredDataAsync(cancellationToken);
        var activeZoneIds = await irrigationRunTracker.GetActiveZoneIdsAsync(cancellationToken);
        var lawnDry = await lawnDryService.GetStatusAsync(cancellationToken);
        return Ok(new IrrigationDashboardSnapshot(
            store.GetSnapshot().Settings.Irrigation,
            stored,
            activeZoneIds,
            decision,
            lawnDry));
    }

    [HttpPost("irrigation/state/reset")]
    public async Task<IActionResult> ResetIrrigationState(CancellationToken cancellationToken)
    {
        await irrigation.ResetStateAsync(cancellationToken);
        return NoContent();
    }

    [HttpPut("irrigation")]
    public async Task<IActionResult> SaveIrrigation([FromBody] IrrigationConfiguration configuration, CancellationToken cancellationToken)
    {
        var settings = store.GetSnapshot().Settings;
        configuration.StateFile = settings.Irrigation.StateFile;
        settings.Irrigation = configuration;
        try
        {
            await store.SaveAsync(settings, cancellationToken);
            irrigation.UpdateConfiguration(configuration);
            return NoContent();
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
    }

    private static T? Find<T>(IEnumerable<T> items, string id) where T : class
    {
        var property = typeof(T).GetProperty("Id");
        return items.FirstOrDefault(x => string.Equals(property?.GetValue(x)?.ToString(), id, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record LoginRequest(string? Password);
    public sealed record IrrigationBlockRequest(string BlockId);
}
