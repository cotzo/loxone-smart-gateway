using loxone.smart.gateway.Api.Irrigation;
using loxone.smart.gateway.Api.PhilipsHue;

namespace loxone.smart.gateway.Api.Gateway;

public sealed class GatewaySettings
{
    public List<LoxoneSettings> LoxoneServers { get; set; } = [];
    public Dictionary<string, LoxoneStateCatalog> LoxoneStateCatalogs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<HueSettings> HueBridges { get; set; } = [];
    public Dictionary<string, HueResourceCatalog> HueResourceCatalogs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<TuyaDeviceSettings> TuyaDevices { get; set; } = [];
    public List<DeviceMapping> Mappings { get; set; } = [];
    public IrrigationConfiguration Irrigation { get; set; } = new();
}

public sealed class LoxoneSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 80;
    public bool UseTls { get; set; }
    public string CertificateAuthorityPem { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class HueSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public string ApplicationKey { get; set; } = string.Empty;
}

public sealed class TuyaDeviceSettings
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string IP { get; set; } = string.Empty;
    public string LocalKey { get; set; } = string.Empty;
    public string Version { get; set; } = "3.4";
}

public sealed class DeviceMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string LoxoneServerId { get; set; } = string.Empty;
    public string LoxoneStateUuid { get; set; } = string.Empty;
    public string TargetType { get; set; } = "hue";
    public string TargetConnectionId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string HueResourceType { get; set; } = "light";
    public PhilipsHueLightType HueLightType { get; set; } = PhilipsHueLightType.DIM;
    public int TransitionTime { get; set; } = 1000;
    public int? TuyaDp { get; set; }
    public MappingValueConverterSettings Converter { get; set; } = new();
}

public sealed class MappingValueConverterSettings
{
    public string Type { get; set; } = "direct";
    public List<ExactValueRule> ExactRules { get; set; } = [];
    public double RangeMinimum { get; set; }
    public double RangeMaximum { get; set; } = 1;
    public string InsideOutputType { get; set; } = "boolean";
    public string InsideOutputValue { get; set; } = "true";
    public string OutsideOutputType { get; set; } = "boolean";
    public string OutsideOutputValue { get; set; } = "false";
}

public sealed class ExactValueRule
{
    public double InputValue { get; set; }
    public string OutputType { get; set; } = "string";
    public string OutputValue { get; set; } = string.Empty;
}

public sealed record LoxoneStateOption(string Uuid, string Control, string Type, string State, string ServerId,
    string ControlUuid = "", string Room = "", string Category = "");
public sealed record LoxoneStateCatalog(DateTimeOffset DiscoveredAt, IReadOnlyList<LoxoneStateOption> States);
public sealed record IntegrationStatus(IReadOnlyList<ConnectionStatus> Loxone, IReadOnlyList<ConnectionStatus> Hue, IReadOnlyList<ConnectionStatus> Tuya, int MappingCount, DateTimeOffset? LastSync);
public sealed record ConnectionStatus(string Id, string Name, string Status);
public sealed record HueResource(string Id, string Type, string Name, string BridgeId, string? RoomId = null, string? RoomName = null, string? ZoneId = null, string? ZoneName = null, IReadOnlyList<string>? MemberIds = null);
public sealed record HueResourceCatalog(DateTimeOffset DiscoveredAt, IReadOnlyList<HueResource> Resources);
public sealed record TuyaDataPoint(string Id, string JsonType, string? Value);

public sealed record SafeGatewaySettings(
    IReadOnlyList<SafeLoxoneSettings> LoxoneServers,
    IReadOnlyDictionary<string, LoxoneStateCatalog> LoxoneStateCatalogs,
    IReadOnlyList<SafeHueSettings> HueBridges,
    IReadOnlyDictionary<string, HueResourceCatalog> HueResourceCatalogs,
    IReadOnlyList<SafeTuyaDeviceSettings> TuyaDevices,
    IReadOnlyList<DeviceMapping> Mappings,
    IrrigationConfiguration Irrigation);
public sealed record SafeLoxoneSettings(string Id, string Name, string Host, int Port, bool UseTls, string CertificateAuthorityPem, string Username, bool HasPassword);
public sealed record SafeHueSettings(string Id, string Name, string Host, bool HasApplicationKey);
public sealed record SafeTuyaDeviceSettings(string Id, string Name, string DeviceId, string IP, string Version, bool HasLocalKey);

