using System.Collections.Concurrent;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class IrrigationRunTracker(IrrigationService irrigationService)
{
    private sealed record ActiveRun(DateTimeOffset StartedAt, string EventId, string Type);
    private readonly ConcurrentDictionary<string, ActiveRun> _active = new(StringComparer.OrdinalIgnoreCase);

    public bool Start(string zoneId, string type = "Irrigation")
    {
        if (!IsValidType(type)) return false;
        var normalized = NormalizeType(type);
        var now = DateTimeOffset.UtcNow;
        var eventId = $"{now:yyyyMMdd-HHmmssfff}-{zoneId}-{Guid.NewGuid():N}";
        _active.TryAdd(zoneId, new ActiveRun(now, eventId, normalized));
        return true;
    }

    public async Task<bool> StopAsync(string zoneId, CancellationToken cancellationToken)
    {
        // Duplicate OFF notifications are harmless.
        if (!_active.TryRemove(zoneId, out var run)) return true;

        var runtimeSeconds = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - run.StartedAt).TotalSeconds));
        return await irrigationService.RecordIrrigationAsync(
            zoneId, runtimeSeconds, run.EventId, run.Type, cancellationToken);
    }

    private static bool IsValidType(string type) =>
        type.Equals("Irrigation", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Rinse", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Manual", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeType(string type) =>
        type.Equals("Rinse", StringComparison.OrdinalIgnoreCase) ? "Rinse" :
        type.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Irrigation";
}
