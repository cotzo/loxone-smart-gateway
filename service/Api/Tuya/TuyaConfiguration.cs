namespace loxone.smart.gateway.Api.Tuya;

public class TuyaConfiguration
{
    public List<TuyaDeviceConfiguration> Devices { get; set; } = [];
}

public class TuyaDeviceConfiguration
{
    public string Name { get; set; }

    public string Id { get; set; }

    public string IP { get; set; }

    public string LocalKey { get; set; }

    // Local protocol version; "3.4" or "3.5" (shown by `tinytuya scan`)
    public string Version { get; set; }
}
