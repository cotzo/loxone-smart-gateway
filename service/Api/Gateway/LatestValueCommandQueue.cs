using System.Threading.Channels;
using Serilog;

namespace loxone.smart.gateway.Api.Gateway;

/// <summary>
/// A single ordered sender for one provider lane. Pending state commands coalesce by target;
/// failed commands retry only while they are still the latest desired value for that target.
/// </summary>
internal sealed class LatestValueCommandQueue<TCommand> : IAsyncDisposable
{
    private sealed record Work(string TargetKey, TCommand Command, long Version, int Attempts, DateTimeOffset EnqueuedAt);

    private readonly Func<TCommand, CancellationToken, Task> _send;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _minimumSendInterval;
    private readonly GatewayMetrics _metrics;
    private readonly string _provider;
    private readonly string _resourceType;
    private readonly string? _bridge;
    private readonly CancellationTokenSource _stop;
    private readonly Channel<string> _ready = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _sync = new();
    private readonly Dictionary<string, Work> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _latestVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _scheduled = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _retryTasks = [];
    private readonly Task _worker;
    private long _nextVersion;
    private DateTimeOffset _lastSendStartedAt = DateTimeOffset.MinValue;

    public LatestValueCommandQueue(
        Func<TCommand, CancellationToken, Task> send,
        TimeSpan minimumSendInterval,
        CancellationToken cancellationToken,
        string queueName,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        GatewayMetrics? metrics = null,
        string provider = "unknown",
        string resourceType = "unknown",
        string? bridge = null)
    {
        _send = send;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _minimumSendInterval = minimumSendInterval;
        _metrics = metrics ?? new GatewayMetrics();
        _provider = provider;
        _resourceType = resourceType;
        _bridge = bridge;
        QueueName = queueName;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = RunAsync();
    }

    public string QueueName { get; }

    public void Enqueue(string targetKey, TCommand command)
    {
        lock (_sync)
        {
            if (_stop.IsCancellationRequested) return;

            var version = ++_nextVersion;
            var replaced = _pending.ContainsKey(targetKey);
            _latestVersions[targetKey] = version;
            _pending[targetKey] = new Work(targetKey, command, version, 0, DateTimeOffset.UtcNow);
            _metrics.RecordEnqueued(_provider, _resourceType, replaced, _bridge);
            ScheduleLocked(targetKey);
        }
    }

    private void ScheduleLocked(string targetKey)
    {
        if (_scheduled.Add(targetKey) && !_ready.Writer.TryWrite(targetKey))
        {
            _scheduled.Remove(targetKey);
            Log.Warning("Could not queue target {Target} on provider lane {Queue}", targetKey, QueueName);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var targetKey in _ready.Reader.ReadAllAsync(_stop.Token))
            {
                Work? work;
                lock (_sync)
                {
                    _scheduled.Remove(targetKey);
                    _pending.Remove(targetKey, out work);
                }
                if (work is null) continue;
                _metrics.RecordDequeued(_provider, _resourceType, _bridge);

                var wait = _minimumSendInterval - (DateTimeOffset.UtcNow - _lastSendStartedAt);
                if (wait > TimeSpan.Zero)
                    await _delay(wait, _stop.Token);

                lock (_sync)
                {
                    if (_latestVersions.GetValueOrDefault(targetKey) != work.Version)
                        continue;
                    _lastSendStartedAt = DateTimeOffset.UtcNow;
                }

                _metrics.RecordQueueWait(_provider, _resourceType, (DateTimeOffset.UtcNow - work.EnqueuedAt).TotalMilliseconds, _bridge);

                try
                {
                    await _send(work.Command, _stop.Token);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ScheduleRetry(work, ex);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private void ScheduleRetry(Work work, Exception exception)
    {
        var attempts = work.Attempts + 1;
        if (attempts >= 10)
        {
            _metrics.RecordFailure(_provider, _resourceType, _bridge);
            Log.Error(exception, "Provider lane {Queue} failed target {Target} after {Attempts} attempts; dropping command",
                QueueName, work.TargetKey, attempts);
            return;
        }

        lock (_sync)
        {
            if (_latestVersions.GetValueOrDefault(work.TargetKey) != work.Version || _pending.ContainsKey(work.TargetKey))
            {
                Log.Information("Not retrying superseded target {Target} on provider lane {Queue}", work.TargetKey, QueueName);
                return;
            }
        }

        var delaySeconds = Math.Min(60, Math.Pow(2, Math.Min(attempts, 6)));
        _metrics.RecordRetry(_provider, _resourceType, _bridge);
        Log.Warning(exception, "Provider lane {Queue} failed target {Target} on attempt {Attempt}; retrying in {RetryDelaySeconds:F0} s",
            QueueName, work.TargetKey, attempts, delaySeconds);
        var retryTask = RetryAfterDelayAsync(work with { Attempts = attempts }, TimeSpan.FromSeconds(delaySeconds));
        lock (_sync)
        {
            _retryTasks.RemoveAll(x => x.IsCompleted);
            _retryTasks.Add(retryTask);
        }
    }

    private async Task RetryAfterDelayAsync(Work retry, TimeSpan delay)
    {
        try
        {
            await _delay(delay, _stop.Token);
            lock (_sync)
            {
                if (_stop.IsCancellationRequested ||
                    _latestVersions.GetValueOrDefault(retry.TargetKey) != retry.Version ||
                    _pending.ContainsKey(retry.TargetKey))
                    return;

                _pending[retry.TargetKey] = retry with { EnqueuedAt = DateTimeOffset.UtcNow };
                _metrics.RecordEnqueued(_provider, _resourceType, replaced: false, _bridge);
                ScheduleLocked(retry.TargetKey);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _ready.Writer.TryComplete();
        try { await _worker; }
        catch (OperationCanceledException) { }

        Task[] retries;
        lock (_sync)
        {
            retries = [.. _retryTasks];
            var remaining = _pending.Count;
            _pending.Clear();
            for (var index = 0; index < remaining; index++)
                _metrics.RecordDequeued(_provider, _resourceType, _bridge);
        }
        try { await Task.WhenAll(retries); }
        catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}
