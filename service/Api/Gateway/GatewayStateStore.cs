using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace loxone.smart.gateway.Api.Gateway;

public sealed class GatewayStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private GatewaySettings _settings;
    private TaskCompletionSource _changed = NewSignal();
    private long _version;
    private DateTimeOffset? _lastSync;
    private readonly Dictionary<string, string> _loxoneStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _hueStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _tuyaStatus = new(StringComparer.OrdinalIgnoreCase);

    public GatewayStateStore(IConfiguration configuration, IDataProtectionProvider protectionProvider)
    {
        var dataDirectory = configuration["Gateway:DataDirectory"] ?? "/data";
        Directory.CreateDirectory(dataDirectory);
        TryRestrictDirectoryPermissions(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "gateway-state.json");
        _protector = protectionProvider.CreateProtector("LoxoneSmartGateway.Settings.v1");
        _settings = Load();
        var irrigationStatePath = Path.Combine(dataDirectory, "irrigation-state.json");
        if (!File.Exists(irrigationStatePath) && !string.IsNullOrWhiteSpace(_settings.Irrigation.StateFile))
        {
            var previousPath = Path.GetFullPath(_settings.Irrigation.StateFile);
            if (!previousPath.Equals(Path.GetFullPath(irrigationStatePath), StringComparison.Ordinal) && File.Exists(previousPath))
                File.Copy(previousPath, irrigationStatePath);
        }
        _settings.Irrigation.StateFile = irrigationStatePath;
        if (File.Exists(irrigationStatePath)) TryRestrictFilePermissions(irrigationStatePath);
    }

    public (GatewaySettings Settings, long Version) GetSnapshot()
    {
        lock (_lock)
            return (Clone(_settings), _version);
    }

    public async Task<LoxoneStateCatalog> SaveLoxoneStateCatalogAsync(
        string serverId,
        IReadOnlyList<LoxoneStateOption> states,
        CancellationToken cancellationToken)
    {
        var settings = GetSnapshot().Settings;
        if (!settings.LoxoneServers.Any(server => server.Id.Equals(serverId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The Miniserver no longer exists in the saved configuration.");

        var catalog = new LoxoneStateCatalog(DateTimeOffset.UtcNow, states);
        settings.LoxoneStateCatalogs[serverId] = catalog;
        await SaveAsync(settings, cancellationToken);
        return catalog;
    }

    public async Task<HueResourceCatalog> SaveHueResourceCatalogAsync(
        string bridgeId,
        IReadOnlyList<HueResource> resources,
        CancellationToken cancellationToken)
    {
        var settings = GetSnapshot().Settings;
        if (!settings.HueBridges.Any(bridge => bridge.Id.Equals(bridgeId, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The Hue bridge no longer exists in the saved configuration.");

        var catalog = new HueResourceCatalog(DateTimeOffset.UtcNow, resources);
        settings.HueResourceCatalogs[bridgeId] = catalog;
        await SaveAsync(settings, cancellationToken);
        return catalog;
    }

    public async Task SaveAsync(GatewaySettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var protectedJson = _protector.Protect(json);
            var tempPath = _filePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, protectedJson, cancellationToken);
            TryRestrictFilePermissions(tempPath);
            File.Move(tempPath, _filePath, true);

            TaskCompletionSource changed;
            lock (_lock)
            {
                _settings = Clone(settings);
                _version++;
                changed = _changed;
                _changed = NewSignal();
            }
            changed.TrySetResult();
        }
        finally { _saveGate.Release(); }
    }

    public Task WaitForChangeAsync(long version, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task changed;
        lock (_lock)
            changed = version == _version ? _changed.Task : Task.CompletedTask;
        return changed.WaitAsync(timeout, cancellationToken);
    }

    public void ReportLoxoneStatus(string id, string status)
    {
        lock (_lock) _loxoneStatus[id] = status;
    }

    public void ReportHueStatus(string id, string status)
    {
        lock (_lock) _hueStatus[id] = status;
    }

    public void ReportTuyaStatus(string id, string status)
    {
        lock (_lock) _tuyaStatus[id] = status;
    }

    public void ReportSync()
    {
        lock (_lock) _lastSync = DateTimeOffset.UtcNow;
    }

    public IntegrationStatus GetStatus()
    {
        lock (_lock)
            return new IntegrationStatus(
                _settings.LoxoneServers.Select(x => new ConnectionStatus(x.Id, x.Name, _loxoneStatus.GetValueOrDefault(x.Id, "Connecting"))).ToList(),
                _settings.HueBridges.Select(x => new ConnectionStatus(x.Id, x.Name, _hueStatus.GetValueOrDefault(x.Id, "Not checked"))).ToList(),
                _settings.TuyaDevices.Select(x => new ConnectionStatus(x.Id, x.Name, _tuyaStatus.GetValueOrDefault(x.Id, "Not checked"))).ToList(),
                _settings.Mappings.Count,
                _lastSync);
    }

    private GatewaySettings Load()
    {
        if (!File.Exists(_filePath))
            return new GatewaySettings();
        try
        {
            var protectedJson = File.ReadAllText(_filePath);
            var settings = JsonSerializer.Deserialize<GatewaySettings>(_protector.Unprotect(protectedJson), JsonOptions) ?? new GatewaySettings();
            // The supported local Loxone communicator uses plain WebSocket on port 80.
            foreach (var server in settings.LoxoneServers) server.Port = 80;
            return settings;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Unable to decrypt or read the persisted gateway settings. Preserve the data volume and its Data Protection keys.", ex);
        }
    }

    private static GatewaySettings Clone(GatewaySettings value) =>
        JsonSerializer.Deserialize<GatewaySettings>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions) ?? new GatewaySettings();

    private static void Validate(GatewaySettings settings)
    {
        if (settings.LoxoneServers.Any(x => x.Port is < 1 or > 65535))
            throw new ArgumentException("Loxone ports must be between 1 and 65535.");
        EnsureUnique(settings.LoxoneServers.Select(x => x.Id), "Loxone server IDs");
        EnsureUnique(settings.HueBridges.Select(x => x.Id), "Hue bridge IDs");
        EnsureUnique(settings.TuyaDevices.Select(x => x.Id), "Tuya device IDs");
        EnsureUnique(settings.Mappings.Select(x => x.Id), "Mapping IDs");
        foreach (var connection in settings.LoxoneServers)
            if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Username) || string.IsNullOrWhiteSpace(connection.Password))
                throw new ArgumentException("Each Loxone server requires a host, username, and password.");
        foreach (var bridge in settings.HueBridges)
            if (string.IsNullOrWhiteSpace(bridge.Host) || string.IsNullOrWhiteSpace(bridge.ApplicationKey))
                throw new ArgumentException("Each Hue bridge requires a host and application key.");
        foreach (var device in settings.TuyaDevices)
            if (string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.DeviceId) || string.IsNullOrWhiteSpace(device.IP) || string.IsNullOrWhiteSpace(device.LocalKey) || device.Version is not ("3.4" or "3.5"))
                throw new ArgumentException("Each Tuya device requires a name, device ID, IP, local key, and protocol version 3.4 or 3.5.");
        foreach (var mapping in settings.Mappings)
        {
            if (string.IsNullOrWhiteSpace(mapping.LoxoneServerId) || string.IsNullOrWhiteSpace(mapping.LoxoneStateUuid) || string.IsNullOrWhiteSpace(mapping.TargetConnectionId) || string.IsNullOrWhiteSpace(mapping.TargetId))
                throw new ArgumentException("Every mapping requires source server/resource and destination connection/resource IDs.");
            if (!settings.LoxoneServers.Any(x => x.Id == mapping.LoxoneServerId))
                throw new ArgumentException("A mapping refers to an unknown Loxone server.");
            if (mapping.TargetType is not ("hue" or "tuya"))
                throw new ArgumentException("Mapping target type must be hue or tuya.");
            if (mapping.ValueMode is not ("direct" or "onOff" or "loxoneDmx"))
                throw new ArgumentException("Value mode must be direct, onOff, or loxoneDmx.");
            if (mapping.TargetType == "tuya" && mapping.TuyaDp is null)
                throw new ArgumentException("Tuya mappings require a datapoint number.");
            if (mapping.TargetType == "hue" && mapping.HueResourceType is not ("light" or "grouped_light"))
                throw new ArgumentException("Hue resource type must be light or grouped_light.");
            if (mapping.TargetType == "hue" && !settings.HueBridges.Any(x => x.Id == mapping.TargetConnectionId))
                throw new ArgumentException("A mapping refers to an unknown Hue bridge.");
            if (mapping.TargetType == "tuya" && !settings.TuyaDevices.Any(x => x.Id == mapping.TargetConnectionId))
                throw new ArgumentException("A mapping refers to an unknown Tuya device.");
            if (mapping.TransitionTime < 0) throw new ArgumentException("Transition time cannot be negative.");
        }

        var irrigation = settings.Irrigation;
        if (irrigation.Latitude is < -90 or > 90 || irrigation.Longitude is < -180 or > 180)
            throw new ArgumentException("Irrigation latitude/longitude are outside their valid ranges.");
        if (irrigation.Zones.Any(x => string.IsNullOrWhiteSpace(x.Id) || x.Exposure < 0 || x.ApplicationRateMmPerHour <= 0))
            throw new ArgumentException("Every irrigation zone requires an ID, non-negative exposure, and positive application rate.");
    }

    private static void EnsureUnique(IEnumerable<string> values, string label)
    {
        if (values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
            throw new ArgumentException($"{label} must be unique.");
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void TryRestrictDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryRestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
