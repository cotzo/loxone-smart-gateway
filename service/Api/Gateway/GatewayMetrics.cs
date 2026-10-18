using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace loxone.smart.gateway.Api.Gateway;

/// <summary>Low-cardinality Prometheus instruments for live provider command delivery.</summary>
public sealed class GatewayMetrics : IDisposable
{
    public const string MeterName = "LoxoneSmartGateway.Gateway";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _commands = null!;
    private readonly Counter<long> _coalesced = null!;
    private readonly Counter<long> _retries = null!;
    private readonly Counter<long> _failures = null!;
    private readonly UpDownCounter<long> _pending = null!;
    private readonly Histogram<double> _queueWait = null!;
    private readonly Histogram<double> _networkDuration = null!;
    private readonly Histogram<double> _eventToDispatch = null!;
    private readonly ConcurrentDictionary<string, QueueAgeSnapshot> _queueOldestPending = new(StringComparer.Ordinal);

    private sealed record QueueAgeSnapshot(string Provider, string ResourceType, string? Bridge, DateTimeOffset? EnqueuedAt);

    public GatewayMetrics()
    {
        _commands = _meter.CreateCounter<long>("gateway.provider.commands", unit: "{command}", description: "Provider commands dispatched, by provider and outcome.");
        _coalesced = _meter.CreateCounter<long>("gateway.provider.commands.coalesced", unit: "{command}", description: "Pending latest-value commands replaced by newer values.");
        _retries = _meter.CreateCounter<long>("gateway.provider.command.retries", unit: "{retry}", description: "Provider command retry attempts scheduled.");
        _failures = _meter.CreateCounter<long>("gateway.provider.command.failures", unit: "{failure}", description: "Provider commands that exhausted retries.");
        _pending = _meter.CreateUpDownCounter<long>("gateway.provider.queue.pending", unit: "{command}", description: "Current unique pending commands in provider queues.");
        _queueWait = _meter.CreateHistogram<double>("gateway.provider.queue.wait", unit: "ms", description: "Time from enqueue to provider send start, including pacing.");
        _networkDuration = _meter.CreateHistogram<double>("gateway.provider.request.duration", unit: "ms", description: "Provider request duration.");
        _eventToDispatch = _meter.CreateHistogram<double>("gateway.provider.event.to.dispatch", unit: "ms", description: "Time from Loxone state receipt to provider send start.");
        _meter.CreateObservableGauge("gateway.provider.queue.oldest_pending.age", ObserveOldestPendingAges,
            unit: "s", description: "Age of the oldest command currently waiting in each provider queue lane.");
    }

    public void RecordEnqueued(string provider, string resourceType, bool replaced, string? bridge = null)
    {
        var tags = Tags(provider, resourceType, bridge: bridge);
        if (replaced) _coalesced.Add(1, tags);
        else _pending.Add(1, tags);
    }

    public void RecordDequeued(string provider, string resourceType, string? bridge = null) => _pending.Add(-1, Tags(provider, resourceType, bridge: bridge));

    public void RecordRetry(string provider, string resourceType, string? bridge = null) => _retries.Add(1, Tags(provider, resourceType, bridge: bridge));

    public void RecordCommand(string provider, string resourceType, string outcome, string? bridge = null) =>
        _commands.Add(1, Tags(provider, resourceType, ("outcome", outcome), bridge));

    public void RecordFailure(string provider, string resourceType, string? bridge = null) => _failures.Add(1, Tags(provider, resourceType, bridge: bridge));

    public void RecordQueueWait(string provider, string resourceType, double milliseconds, string? bridge = null) =>
        _queueWait.Record(Math.Max(0, milliseconds), Tags(provider, resourceType, bridge: bridge));

    public void RecordRequestDuration(string provider, string resourceType, double milliseconds, string outcome, string? bridge = null) =>
        _networkDuration.Record(Math.Max(0, milliseconds), Tags(provider, resourceType, ("outcome", outcome), bridge));

    public void RecordEventToDispatch(string provider, string resourceType, double milliseconds, string? bridge = null) =>
        _eventToDispatch.Record(Math.Max(0, milliseconds), Tags(provider, resourceType, bridge: bridge));

    public void RecordOldestPending(string queue, string provider, string resourceType, string? bridge, DateTimeOffset? enqueuedAt) =>
        _queueOldestPending[queue] = new QueueAgeSnapshot(provider, resourceType, bridge, enqueuedAt);

    private IEnumerable<Measurement<double>> ObserveOldestPendingAges()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var snapshot in _queueOldestPending.Values)
        {
            var tags = Tags(snapshot.Provider, snapshot.ResourceType, bridge: snapshot.Bridge);
            var ageSeconds = snapshot.EnqueuedAt is { } enqueuedAt
                ? Math.Max(0, (now - enqueuedAt).TotalSeconds)
                : 0;
            yield return new Measurement<double>(ageSeconds, tags);
        }
    }

    private static TagList Tags(string provider, string resourceType, (string Key, string Value)? extra = null, string? bridge = null)
    {
        var tags = new TagList { { "provider", provider }, { "resource_type", resourceType } };
        if (extra is { } item) tags.Add(item.Key, item.Value);
        if (!string.IsNullOrWhiteSpace(bridge) && provider == "hue") tags.Add("bridge", bridge);
        return tags;
    }

    public void Dispose() => _meter.Dispose();
}
