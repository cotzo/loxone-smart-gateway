using System.Diagnostics.Metrics;
using System.Diagnostics;

namespace loxone.smart.gateway.Api.Gateway;

/// <summary>Low-cardinality Prometheus instruments for live provider command delivery.</summary>
public sealed class GatewayMetrics : IDisposable
{
    public const string MeterName = "LoxoneSmartGateway.Gateway";
    private readonly Meter _meter = new(MeterName);
    private readonly Counter<long> _commands = null!;
    private readonly Counter<long> _coalesced = null!;
    private readonly Counter<long> _hueGroupOptimizations = null!;
    private readonly Counter<long> _hueGroupRequests = null!;
    private readonly Counter<long> _hueGroupRequestsSaved = null!;
    private readonly Counter<long> _retries = null!;
    private readonly Counter<long> _failures = null!;
    private readonly UpDownCounter<long> _pending = null!;
    private readonly Histogram<double> _queueWait = null!;
    private readonly Histogram<double> _networkDuration = null!;
    private readonly Histogram<double> _eventToDispatch = null!;

    public GatewayMetrics()
    {
        _commands = _meter.CreateCounter<long>("gateway.provider.commands", unit: "{command}", description: "Provider commands dispatched, by provider and outcome.");
        _coalesced = _meter.CreateCounter<long>("gateway.provider.commands.coalesced", unit: "{command}", description: "Pending latest-value commands replaced by newer values.");
        _hueGroupOptimizations = _meter.CreateCounter<long>("gateway.hue.group.optimizations", unit: "{optimization}", description: "Hue grouped-light command bundles replaced with one larger group request.");
        _hueGroupRequests = _meter.CreateCounter<long>("gateway.hue.group.requests", unit: "{request}", description: "Individual Hue grouped-light requests represented by group optimizations.");
        _hueGroupRequestsSaved = _meter.CreateCounter<long>("gateway.hue.group.requests.saved", unit: "{request}", description: "Hue grouped-light requests avoided by exact-coverage group optimizations.");
        _retries = _meter.CreateCounter<long>("gateway.provider.command.retries", unit: "{retry}", description: "Provider command retry attempts scheduled.");
        _failures = _meter.CreateCounter<long>("gateway.provider.command.failures", unit: "{failure}", description: "Provider commands that exhausted retries.");
        _pending = _meter.CreateUpDownCounter<long>("gateway.provider.queue.pending", unit: "{command}", description: "Current unique pending commands in provider queues.");
        _queueWait = _meter.CreateHistogram<double>("gateway.provider.queue.wait", unit: "ms", description: "Time from enqueue to provider send start, including pacing.");
        _networkDuration = _meter.CreateHistogram<double>("gateway.provider.request.duration", unit: "ms", description: "Provider request duration.");
        _eventToDispatch = _meter.CreateHistogram<double>("gateway.provider.event.to.dispatch", unit: "ms", description: "Time from Loxone state receipt to provider send start.");
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

    public void RecordHueGroupOptimization(int requestCount, string? bridge = null)
    {
        if (requestCount < 2) return;
        var tags = Tags("hue", "grouped_light", bridge: bridge);
        _hueGroupOptimizations.Add(1, tags);
        _hueGroupRequests.Add(requestCount, tags);
        _hueGroupRequestsSaved.Add(requestCount - 1, tags);
    }

    public void RecordFailure(string provider, string resourceType, string? bridge = null) => _failures.Add(1, Tags(provider, resourceType, bridge: bridge));

    public void RecordQueueWait(string provider, string resourceType, double milliseconds, string? bridge = null) =>
        _queueWait.Record(Math.Max(0, milliseconds), Tags(provider, resourceType, bridge: bridge));

    public void RecordRequestDuration(string provider, string resourceType, double milliseconds, string outcome, string? bridge = null) =>
        _networkDuration.Record(Math.Max(0, milliseconds), Tags(provider, resourceType, ("outcome", outcome), bridge));

    public void RecordEventToDispatch(string provider, string resourceType, double milliseconds, string? bridge = null) =>
        _eventToDispatch.Record(Math.Max(0, milliseconds), Tags(provider, resourceType, bridge: bridge));

    private static TagList Tags(string provider, string resourceType, (string Key, string Value)? extra = null, string? bridge = null)
    {
        var tags = new TagList { { "provider", provider }, { "resource_type", resourceType } };
        if (extra is { } item) tags.Add(item.Key, item.Value);
        if (!string.IsNullOrWhiteSpace(bridge) && provider == "hue") tags.Add("bridge", bridge);
        return tags;
    }

    public void Dispose() => _meter.Dispose();
}
