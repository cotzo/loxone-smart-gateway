using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using loxone.smart.gateway.Api.Gateway;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed record IrrigationBlockOption(string Id, string ServerId, string ServerName, string Control,
    string ActiveStateUuid, string ZonesStateUuid, string CurrentZoneStateUuid);
public sealed record ImportedIrrigationZone(string LoxoneZoneId, string Name, int DurationSeconds);
public sealed record IrrigationBlockPreview(IrrigationBlockOption Block, bool Active, int CurrentZone,
    IReadOnlyList<ImportedIrrigationZone> Zones);

public sealed class IrrigationLoxoneService(GatewayStateStore store, LoxoneIntegration loxone,
    IrrigationService irrigation, IrrigationRunTracker runTracker)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<string>> _activeByBlock = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (bool? Active, int? CurrentZone)> _observedStates = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IrrigationBlockOption> GetBlocks()
    {
        var settings = store.GetSnapshot().Settings;
        return settings.LoxoneStateCatalogs.SelectMany(catalog =>
        {
            var server = settings.LoxoneServers.FirstOrDefault(x => x.Id.Equals(catalog.Key, StringComparison.OrdinalIgnoreCase));
            if (server is null) return Enumerable.Empty<IrrigationBlockOption>();
            return catalog.Value.States.Where(x => x.Type.Contains("irrigation", StringComparison.OrdinalIgnoreCase))
                .GroupBy(x => (x.ServerId, x.Control))
                .Where(group => group.Any(x => x.State.Equals("active", StringComparison.OrdinalIgnoreCase)) &&
                                group.Any(x => x.State.Equals("zones", StringComparison.OrdinalIgnoreCase)) &&
                                group.Any(x => x.State.Equals("currentZone", StringComparison.OrdinalIgnoreCase)))
                .Select(group => new IrrigationBlockOption(
                    MakeBlockId(group.Key.ServerId, group.Key.Control), group.Key.ServerId, server.Name,
                    group.Key.Control,
                    group.First(x => x.State.Equals("active", StringComparison.OrdinalIgnoreCase)).Uuid,
                    group.First(x => x.State.Equals("zones", StringComparison.OrdinalIgnoreCase)).Uuid,
                    group.First(x => x.State.Equals("currentZone", StringComparison.OrdinalIgnoreCase)).Uuid));
        }).OrderBy(x => x.ServerName).ThenBy(x => x.Control).ToList();
    }

    public async Task<IrrigationBlockPreview> PreviewAsync(string blockId, CancellationToken cancellationToken)
    {
        var block = FindBlock(blockId);
        var settings = store.GetSnapshot().Settings;
        var server = settings.LoxoneServers.First(x => x.Id.Equals(block.ServerId, StringComparison.OrdinalIgnoreCase));
        var values = await loxone.ReadStatesOnceAsync(server,
            new HashSet<string>([block.ActiveStateUuid, block.ZonesStateUuid, block.CurrentZoneStateUuid], StringComparer.OrdinalIgnoreCase), cancellationToken);
        if (!values.TryGetValue(NormalizeUuid(block.ZonesStateUuid), out var zonesRaw) ||
            !values.TryGetValue(NormalizeUuid(block.CurrentZoneStateUuid), out var currentRaw) ||
            !values.TryGetValue(NormalizeUuid(block.ActiveStateUuid), out var activeRaw))
            throw new InvalidOperationException("The Miniserver did not return all required states for this irrigation block.");
        return new IrrigationBlockPreview(block, ParseActive(activeRaw), ParseCurrentZone(currentRaw), ParseZones(zonesRaw));
    }

    public async Task<IrrigationBlockPreview> ImportAsync(string blockId, CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(blockId, cancellationToken);
        var settings = store.GetSnapshot().Settings;
        var config = settings.Irrigation;
        var previousBlockId = config.LoxoneBlock is null ? null : MakeBlockId(config.LoxoneBlock.ServerId, config.LoxoneBlock.Control);
        var imported = new List<IrrigationZoneConfiguration>();
        foreach (var source in preview.Zones)
        {
            var existing = config.Zones.FirstOrDefault(zone =>
                zone.LoxoneZoneId.Equals(source.LoxoneZoneId, StringComparison.OrdinalIgnoreCase) &&
                zone.LoxoneBlockId.Equals(preview.Block.Id, StringComparison.OrdinalIgnoreCase));
            existing ??= config.Zones.FirstOrDefault(zone => string.IsNullOrWhiteSpace(zone.LoxoneBlockId) &&
                zone.Id.Equals(source.LoxoneZoneId, StringComparison.OrdinalIgnoreCase));
            existing ??= new IrrigationZoneConfiguration
            {
                Id = CreateGatewayZoneId(preview.Block, source.LoxoneZoneId),
                Type = "Other",
                Exposure = 1,
                ApplicationRateMmPerHour = 10
            };
            existing.LoxoneZoneId = source.LoxoneZoneId;
            existing.LoxoneBlockId = preview.Block.Id;
            existing.LoxoneName = source.Name;
            if (string.IsNullOrWhiteSpace(existing.Id)) existing.Id = CreateGatewayZoneId(preview.Block, source.LoxoneZoneId);
            if (!imported.Any(zone => zone.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))) imported.Add(existing);
        }
        // Keep configured zones not present in a new export: deleting them could invalidate stored balance history.
        imported.AddRange(config.Zones.Where(zone => !imported.Any(x => x.Id.Equals(zone.Id, StringComparison.OrdinalIgnoreCase))));
        config.Zones = imported;
        config.LoxoneBlock = new IrrigationLoxoneBlockConfiguration
        {
            ServerId = preview.Block.ServerId,
            Control = preview.Block.Control,
            ActiveStateUuid = preview.Block.ActiveStateUuid,
            ZonesStateUuid = preview.Block.ZonesStateUuid,
            CurrentZoneStateUuid = preview.Block.CurrentZoneStateUuid
        };
        await store.SaveAsync(settings, cancellationToken);
        irrigation.UpdateConfiguration(config);
        if (previousBlockId is not null && !previousBlockId.Equals(preview.Block.Id, StringComparison.OrdinalIgnoreCase))
        {
            string[] previousActive;
            lock (_sync)
            {
                previousActive = _activeByBlock.Remove(previousBlockId, out var active) ? active.ToArray() : [];
                _observedStates.Remove(previousBlockId);
            }
            foreach (var zoneId in previousActive)
                await runTracker.StopAsync(zoneId, cancellationToken);
        }
        lock (_sync) _observedStates[preview.Block.Id] = (preview.Active, preview.CurrentZone);
        await ApplyTrackedStateAsync(preview.Block.ServerId, preview.Active ? "true" : "false", cancellationToken);
        return preview;
    }

    public async Task ObserveStateAsync(string serverId, string uuid, string value, CancellationToken cancellationToken)
    {
        var block = store.GetSnapshot().Settings.Irrigation.LoxoneBlock;
        if (block is null || !block.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase)) return;
        var isActive = NormalizeUuid(block.ActiveStateUuid).Equals(NormalizeUuid(uuid), StringComparison.OrdinalIgnoreCase);
        var isCurrentZone = NormalizeUuid(block.CurrentZoneStateUuid).Equals(NormalizeUuid(uuid), StringComparison.OrdinalIgnoreCase);
        if (!isActive && !isCurrentZone) return;
        var key = MakeBlockId(block.ServerId, block.Control);
        lock (_sync)
        {
            _observedStates.TryGetValue(key, out var current);
            _observedStates[key] = isActive ? (TryParseActive(value), current.CurrentZone) : (current.Active, TryParseCurrentZone(value));
        }
        await ApplyTrackedStateAsync(serverId, value, cancellationToken);
    }

    private async Task ApplyTrackedStateAsync(string serverId, string raw, CancellationToken cancellationToken)
    {
        var config = store.GetSnapshot().Settings.Irrigation;
        var block = config.LoxoneBlock;
        if (block is null || !block.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase)) return;
        var key = MakeBlockId(block.ServerId, block.Control);
        (bool? Active, int? CurrentZone) observed;
        lock (_sync) _observedStates.TryGetValue(key, out observed);
        if (observed.Active is null || observed.CurrentZone is null)
        {
            Log.Debug("Waiting for both active and currentZone values from Loxone irrigation block {Control}; latest event was {Value}", block.Control, raw);
            return;
        }
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blockZones = config.Zones.Where(x => x.LoxoneBlockId.Equals(MakeBlockId(block.ServerId, block.Control), StringComparison.OrdinalIgnoreCase)).ToList();
        var current = observed.CurrentZone.Value;
        if (observed.Active.Value && current == 8) expected.UnionWith(blockZones.Select(x => x.Id));
        else if (observed.Active.Value && current >= 0 && current < blockZones.Count)
        {
            var zone = blockZones.ElementAtOrDefault(current);
            if (zone is not null) expected.Add(zone.Id);
        }

        HashSet<string> previous;
        lock (_sync)
        {
            if (!_activeByBlock.TryGetValue(key, out previous!))
            {
                previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _activeByBlock[key] = previous;
            }
        }
        foreach (var id in previous.Except(expected).ToArray())
            if (await runTracker.StopAsync(id, cancellationToken)) previous.Remove(id);
        foreach (var id in expected.Except(previous).ToArray())
            if (await runTracker.StartAsync(id, cancellationToken)) previous.Add(id);
        Log.Information("Loxone irrigation block {Control} active={Active}, currentZone={CurrentZone} now tracks active gateway zones [{ZoneIds}]",
            block.Control, observed.Active, current, string.Join(",", expected));
    }

    private IrrigationBlockOption FindBlock(string id) => GetBlocks().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException("The selected irrigation function block was not found. Refresh Loxone resources and select it again.");

    private static IReadOnlyList<ImportedIrrigationZone> ParseZones(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("The Loxone zones state is not a JSON array.");
        var zones = new List<ImportedIrrigationZone>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idNode) ? idNode.ToString() : string.Empty;
            var name = item.TryGetProperty("name", out var nameNode) ? nameNode.ToString() : string.Empty;
            var duration = item.TryGetProperty("duration", out var durationNode) && durationNode.TryGetInt32(out var seconds) ? seconds : 0;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || duration < 0)
                throw new InvalidOperationException("The Loxone zones state contains an entry without a valid id/name/duration.");
            if (zones.Any(x => x.LoxoneZoneId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"The Loxone zones state contains duplicate zone id '{id}'.");
            zones.Add(new ImportedIrrigationZone(id, name, duration));
        }
        if (zones.Count is < 1 or > 8) throw new InvalidOperationException("The selected Loxone block must contain between 1 and 8 zones.");
        return zones;
    }

    private static int ParseCurrentZone(string raw) => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) && current is >= -1 and <= 8
        ? current : throw new InvalidOperationException($"Invalid Loxone currentZone value '{raw}'. Expected -1 through 8.");

    private static bool ParseActive(string raw) => TryParseActive(raw) ?? throw new InvalidOperationException($"Invalid Loxone active value '{raw}'. Expected true/false or 1/0.");
    private static bool? TryParseActive(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null
    };
    private static int? TryParseCurrentZone(string raw) => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var current) && current is >= -1 and <= 8 ? current : null;

    private static string CreateGatewayZoneId(IrrigationBlockOption block, string zoneId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{block.ServerId}|{block.Control}|{zoneId}")))[..16].ToLowerInvariant();
        return "loxone-" + hash;
    }

    private static string MakeBlockId(string serverId, string control) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{serverId}|{control}")))[..24].ToLowerInvariant();
    private static string NormalizeUuid(string uuid) => uuid.Replace("-", string.Empty, StringComparison.Ordinal);
}
