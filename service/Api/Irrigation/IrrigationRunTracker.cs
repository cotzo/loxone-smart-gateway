namespace loxone.smart.gateway.Api.Irrigation;

public sealed class IrrigationRunTracker
{
    private sealed record ActiveRun(DateTimeOffset StartedAt, string EventId);

    private readonly IrrigationService _irrigationService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ActiveRun> _active;

    public IrrigationRunTracker(IrrigationService irrigationService)
    {
        _irrigationService = irrigationService;
        // Active time is process-local now that Loxone currentZone drives tracking. A persisted
        // active run from a previous process has no trustworthy stop time and must not inflate
        // applied water or leave the mower permanently interlocked after a restart.
        _active = new Dictionary<string, ActiveRun>(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> StartAsync(string zoneId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Duplicate ON notifications are harmless and retain the original start timestamp.
            if (_active.ContainsKey(zoneId)) return true;

            var now = DateTimeOffset.UtcNow;
            var eventId = $"{now:yyyyMMdd-HHmmssfff}-{zoneId}-{Guid.NewGuid():N}";
            _active[zoneId] = new ActiveRun(now, eventId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> StopAsync(string zoneId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Duplicate OFF notifications are harmless.
            if (!_active.TryGetValue(zoneId, out var run)) return true;

            var runtimeSeconds = Math.Max(1, (int)Math.Round((DateTimeOffset.UtcNow - run.StartedAt).TotalSeconds));

            // Keep the active run until accounting succeeds. If this call is cancelled, fails, or
            // throws, a later Loxone retry still has the original start time and event id.
            var recorded = await _irrigationService.RecordIrrigationAsync(
                zoneId, runtimeSeconds, run.EventId, cancellationToken);
            if (!recorded) return false;

            _active.Remove(zoneId);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> IsAnyZoneActiveAsync(IReadOnlySet<string> zoneIds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _active.Keys.Any(zoneIds.Contains);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetActiveZoneIdsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return _active.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }
}
