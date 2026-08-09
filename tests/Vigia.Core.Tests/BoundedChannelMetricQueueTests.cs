using Microsoft.Extensions.Options;
using Vigia.Api.Queue;

namespace Vigia.Core.Tests;

public class BoundedChannelMetricQueueTests
{
    private static BoundedChannelMetricQueue Queue(int capacity, int timeoutMs = 50) =>
        new(Options.Create(new QueueOptions
        {
            Capacity = capacity,
            EnqueueTimeoutMilliseconds = timeoutMs,
        }));

    private static MetricBatch Batch(string source = "host")
    {
        MetricName.TryCreate("cpu.usage", out var name);
        return new MetricBatch(1, source,
            [new MetricPoint(name, "percent", DateTimeOffset.UnixEpoch, 1.0)]);
    }

    [Fact]
    public async Task AcceptsBatchesWhileCapacityRemains()
    {
        var queue = Queue(capacity: 4);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(await queue.TryEnqueueAsync(Batch(), default));
        }

        Assert.Equal(4, queue.Depth);
    }

    [Fact]
    public async Task RefusesInsteadOfBufferingWhenSaturated()
    {
        // The whole point of the bound: shed load at a known boundary rather than
        // grow the heap until the process dies.
        var queue = Queue(capacity: 2);

        Assert.True(await queue.TryEnqueueAsync(Batch(), default));
        Assert.True(await queue.TryEnqueueAsync(Batch(), default));
        Assert.False(await queue.TryEnqueueAsync(Batch(), default));

        Assert.Equal(2, queue.Depth);
    }

    [Fact]
    public async Task AcceptsAgainOnceAConsumerDrains()
    {
        var queue = Queue(capacity: 1);

        Assert.True(await queue.TryEnqueueAsync(Batch("first"), default));
        Assert.False(await queue.TryEnqueueAsync(Batch("second"), default));

        await foreach (var drained in queue.DequeueAllAsync(default))
        {
            Assert.Equal("first", drained.SourceName);
            break;
        }

        Assert.True(await queue.TryEnqueueAsync(Batch("third"), default));
    }

    [Fact]
    public async Task DequeueYieldsBatchesInOrder()
    {
        var queue = Queue(capacity: 8);
        await queue.TryEnqueueAsync(Batch("a"), default);
        await queue.TryEnqueueAsync(Batch("b"), default);

        var seen = new List<string>();
        using var cts = new CancellationTokenSource();

        await foreach (var batch in queue.DequeueAllAsync(cts.Token))
        {
            seen.Add(batch.SourceName);
            if (seen.Count == 2)
            {
                await cts.CancelAsync();
                break;
            }
        }

        Assert.Equal(["a", "b"], seen);
    }

    [Fact]
    public async Task DepthReturnsToZeroAfterDraining()
    {
        var queue = Queue(capacity: 4);
        await queue.TryEnqueueAsync(Batch(), default);
        await queue.TryEnqueueAsync(Batch(), default);

        var drained = 0;
        await foreach (var _ in queue.DequeueAllAsync(default))
        {
            if (++drained == 2)
            {
                break;
            }
        }

        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public async Task ConcurrentProducersAllSucceedWhenQueueHasRoom()
    {
        // A single-attempt WaitToWriteAsync + TryWrite can lose a race between
        // concurrent producers waking for the same freed slot: one wins, the other's
        // TryWrite fails and gets reported as saturation even though the queue had
        // room. Capacity is kept far above the producer count so genuine saturation
        // cannot occur, isolating the lost-race failure mode.
        const int producers = 32;
        var queue = Queue(capacity: 256);

        var results = await Task.WhenAll(
            Enumerable.Range(0, producers)
                .Select(_ => queue.TryEnqueueAsync(Batch(), default).AsTask()));

        Assert.All(results, Assert.True);
        Assert.Equal(producers, queue.Depth);
    }

    [Fact]
    public async Task CallerCancellationPropagatesRatherThanReturningFalse()
    {
        // A caller hangup must never be reported as backpressure: the queue being
        // saturated and the client cancelling are distinct outcomes with distinct
        // meanings (429 vs. abandoned request), and collapsing them loses that
        // distinction.
        var queue = Queue(capacity: 1);
        Assert.True(await queue.TryEnqueueAsync(Batch(), default));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queue.TryEnqueueAsync(Batch(), cts.Token).AsTask());
    }

    [Fact]
    public async Task SaturationStillReturnsFalseWhenCallerTokenIsHealthy()
    {
        // Pins the original contract: with no caller cancellation in play, running
        // out of room still yields a plain `false`, not an exception.
        var queue = Queue(capacity: 1);

        Assert.True(await queue.TryEnqueueAsync(Batch(), default));
        Assert.False(await queue.TryEnqueueAsync(Batch(), default));
    }
}
