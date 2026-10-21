using loxone.smart.gateway.Api.Gateway;

namespace loxone.smart.gateway.Api.Irrigation;

/// <summary>Tracks the configured Loxone winter/system-emptying interlock. Unknown is fail-closed.</summary>
public sealed class IrrigationDisableStateService(GatewayStateStore store)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (bool Disabled, string Raw, DateTimeOffset At)> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _connected = new(StringComparer.OrdinalIgnoreCase);
    private string? _configuredMappingKey;

    public void ReportConnection(string serverId, bool connected)
    {
        lock (_sync) _connected[serverId] = connected;
    }

    public void ObserveState(string serverId, string uuid, string raw)
    {
        var mapping = store.GetSnapshot().Settings.Irrigation.IrrigationDisabledState;
        if (mapping is null || !mapping.ServerId.Equals(serverId, StringComparison.OrdinalIgnoreCase) ||
            !Normalize(mapping.StateUuid).Equals(Normalize(uuid), StringComparison.OrdinalIgnoreCase)) return;

        var normalized = raw.Trim().ToLowerInvariant();
        if (normalized is not ("1" or "0" or "true" or "false" or "on" or "off"))
        {
            lock (_sync) _values.Remove(Key(serverId, uuid));
            Serilog.Log.Warning("Irrigation interlock state from {ServerId} was not boolean ({Value}); irrigation remains disabled", serverId, raw);
            return;
        }
        lock (_sync)
        {
            ResetForMappingChange(mapping);
            _values[Key(serverId, uuid)] = (normalized is "1" or "true" or "on", raw, DateTimeOffset.UtcNow);
        }
    }

    public IrrigationInterlockStatus GetStatus()
    {
        var mapping = store.GetSnapshot().Settings.Irrigation.IrrigationDisabledState;
        if (mapping is null)
        {
            lock (_sync) { _values.Clear(); _configuredMappingKey = null; }
            return new(false, false, true, true, null, null, "No irrigation-disable switch configured.");
        }
        if (string.IsNullOrWhiteSpace(mapping.ServerId) || string.IsNullOrWhiteSpace(mapping.StateUuid))
            return new(true, true, false, false, null, null, "Disabled: the configured irrigation-disable switch is incomplete.");

        lock (_sync)
        {
            ResetForMappingChange(mapping);
            var connected = _connected.GetValueOrDefault(mapping.ServerId);
            if (!connected)
                return new(true, true, false, false, null, null, "Disabled: Miniserver is disconnected or has not delivered an initial state.");
            if (!_values.TryGetValue(Key(mapping.ServerId, mapping.StateUuid), out var value))
                return new(true, true, false, true, null, null, "Disabled: waiting for a valid switch state.");
            return new(true, value.Disabled, true, true, value.Raw, value.At,
                value.Disabled ? "Disabled by the selected Loxone switch." : "Allowed by the selected Loxone switch.");
        }
    }

    public bool IsAllowed => !GetStatus().Disabled;

    private static string Key(string serverId, string uuid) => $"{serverId}|{Normalize(uuid)}";
    private static string Normalize(string value) => value.Replace("-", string.Empty, StringComparison.Ordinal);

    private void ResetForMappingChange(IrrigationLoxoneStateMapping mapping)
    {
        var mappingKey = Key(mapping.ServerId, mapping.StateUuid);
        if (string.Equals(mappingKey, _configuredMappingKey, StringComparison.OrdinalIgnoreCase)) return;
        _values.Clear();
        _configuredMappingKey = mappingKey;
    }
}
