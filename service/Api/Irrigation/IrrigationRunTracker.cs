using System.Text.Json;
using Serilog;

namespace loxone.smart.gateway.Api.Irrigation;

public sealed class IrrigationRunTracker
{
    private sealed record ActiveRun(DateTimeOffset StartedAt, string EventId, string Type);

    private readonly IrrigationService _irrigationService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _activeRunsFile;
    private readonly Dictionary<string, ActiveRun> _active;

    public IrrigationRunTracker(IrrigationService irrigationService, IConfiguration configuration)
    {
        _irrigationService = irrigationService;
        var stateFile = configuration["Api:IrrigationConfiguration:StateFile"] ?? "data/irrigation-state.json";
        _activeRunsFile = stateFile + ".active-runs.json";
        _active = LoadActiveRuns();
    }

    public async Task<bool> StartAsync(string zoneId, string type = "Irrigation", CancellationToken cancellationToken = default)
    {
        if (!IsValidType(type)) return false;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Duplicate ON notifications are harmless and retain the original start timestamp.
            if (_active.ContainsKey(zoneId)) return true;

            var normalized = NormalizeType(type);
            var now = DateTimeOffset.UtcNow;
            var eventId = $"{now:yyyyMMdd-HHmmssfff}-{zoneId}-{Guid.NewGuid():N}";
            _active[zoneId] = new ActiveRun(now, eventId, normalized);
            await PersistAsync(cancellationToken);
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
                zoneId, runtimeSeconds, run.EventId, run.Type, cancellationToken);
            if (!recorded) return false;

            _active.Remove(zoneId);
            await PersistAsync(cancellationToken);
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

    private Dictionary<string, ActiveRun> LoadActiveRuns()
    {
        try
        {
            if (!File.Exists(_activeRunsFile))
                return new Dictionary<string, ActiveRun>(StringComparer.OrdinalIgnoreCase);

            var loaded = JsonSerializer.Deserialize<Dictionary<string, ActiveRun>>(File.ReadAllText(_activeRunsFile));
            return loaded is null
                ? new Dictionary<string, ActiveRun>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ActiveRun>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to load active irrigation runs from {file}", _activeRunsFile);
            return new Dictionary<string, ActiveRun>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_activeRunsFile);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        var temp = _activeRunsFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(_active), cancellationToken);
        File.Move(temp, _activeRunsFile, true);
    }

    private static bool IsValidType(string type) =>
        type.Equals("Irrigation", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Rinse", StringComparison.OrdinalIgnoreCase) ||
        type.Equals("Manual", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeType(string type) =>
        type.Equals("Rinse", StringComparison.OrdinalIgnoreCase) ? "Rinse" :
        type.Equals("Manual", StringComparison.OrdinalIgnoreCase) ? "Manual" : "Irrigation";
}
