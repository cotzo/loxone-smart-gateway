namespace loxone.smart.gateway.Api.Tuya;

/// <summary>Connection details for a Tuya device used by the live integration.</summary>
public sealed class TuyaDeviceConfiguration
{
    public string Name { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string IP { get; set; } = string.Empty;
    public string LocalKey { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}
