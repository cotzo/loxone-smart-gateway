using loxone.smart.gateway.Api.Gateway;
using Xunit;

namespace service.tests;

public sealed class LatestValueCommandQueueTests
{
    [Fact]
    public async Task CoalescesPendingValuesForTheSameTargetButDoesNotInterruptInflightSend()
    {
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondSent = NewSignal();
        var sent = new List<int>();
        await using var queue = new LatestValueCommandQueue<int>(async (value, _) =>
        {
            lock (sent) sent.Add(value);
            if (value == 1)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }
            else secondSent.TrySetResult();
        }, TimeSpan.Zero, CancellationToken.None, "test");

        queue.Enqueue("lamp", 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue("lamp", 2);
        queue.Enqueue("lamp", 3);
        releaseFirst.TrySetResult();
        await secondSent.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (sent) Assert.Equal([1, 3], sent);
    }

    [Fact]
    public async Task IndependentQueuesCanSendConcurrently()
    {
        var firstStarted = NewSignal();
        var releaseFirst = NewSignal();
        var secondStarted = NewSignal();
        await using var first = new LatestValueCommandQueue<int>(async (_, _) =>
        {
            firstStarted.TrySetResult();
            await releaseFirst.Task;
        }, TimeSpan.Zero, CancellationToken.None, "Hue bridge A lights");
        await using var second = new LatestValueCommandQueue<int>((_, _) =>
        {
            secondStarted.TrySetResult();
            return Task.CompletedTask;
        }, TimeSpan.Zero, CancellationToken.None, "Hue bridge B lights");

        first.Enqueue("lamp-a", 1);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        second.Enqueue("lamp-b", 1);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFirst.TrySetResult();
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task AppliesConfiguredPacingBeforeNextSend(int intervalMilliseconds)
    {
        var sentTwice = NewSignal();
        var requestedDelays = new List<TimeSpan>();
        var sent = 0;
        await using var queue = new LatestValueCommandQueue<int>((_, _) =>
        {
            if (Interlocked.Increment(ref sent) == 2) sentTwice.TrySetResult();
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(intervalMilliseconds), CancellationToken.None, "test",
            (delay, _) =>
            {
                lock (requestedDelays) requestedDelays.Add(delay);
                return Task.CompletedTask;
            });

        queue.Enqueue("lamp-a", 1);
        await WaitUntilAsync(() => Volatile.Read(ref sent) == 1);
        queue.Enqueue("lamp-b", 2);
        await sentTwice.Task.WaitAsync(TimeSpan.FromSeconds(2));

        lock (requestedDelays)
            Assert.Contains(requestedDelays, delay => delay.TotalMilliseconds >= intervalMilliseconds - 10);
    }

    [Fact]
    public async Task DoesNotRetryFailedValueAfterANewerValueIsQueued()
    {
        var firstAttemptFailed = NewSignal();
        var retryDelayStarted = NewSignal();
        var releaseRetryDelay = NewSignal();
        var latestSent = NewSignal();
        var sent = new List<int>();
        await using var queue = new LatestValueCommandQueue<int>((value, _) =>
        {
            lock (sent) sent.Add(value);
            if (value == 1)
            {
                firstAttemptFailed.TrySetResult();
                throw new InvalidOperationException("temporary failure");
            }
            latestSent.TrySetResult();
            return Task.CompletedTask;
        }, TimeSpan.Zero, CancellationToken.None, "test", async (delay, token) =>
        {
            if (delay >= TimeSpan.FromSeconds(2))
            {
                retryDelayStarted.TrySetResult();
                await releaseRetryDelay.Task.WaitAsync(token);
            }
        });

        queue.Enqueue("lamp", 1);
        await firstAttemptFailed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await retryDelayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue("lamp", 2);
        await latestSent.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseRetryDelay.TrySetResult();
        await Task.Delay(50);

        lock (sent) Assert.Equal([1, 2], sent);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout) throw new TimeoutException("Condition was not reached.");
            await Task.Delay(1);
        }
    }
}
