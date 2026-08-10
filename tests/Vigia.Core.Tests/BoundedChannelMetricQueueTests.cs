using System.Reflection;
using System.Threading.Channels;
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

    /// <summary>
    /// Writes directly to the queue's underlying channel, bypassing
    /// <see cref="BoundedChannelMetricQueue.TryEnqueueAsync"/> and its enqueue
    /// timeout entirely. Needed to saturate a queue configured with
    /// <c>EnqueueTimeoutMilliseconds: 0</c> for setup: going through
    /// <c>TryEnqueueAsync</c> there would itself race the near-instant timeout,
    /// even though the queue has room, defeating the point of the setup step.
    /// </summary>
    private static void FillDirectly(BoundedChannelMetricQueue queue, MetricBatch batch)
    {
        var channel = (Channel<MetricBatch>)typeof(BoundedChannelMetricQueue)
            .GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(queue)!;

        Assert.True(channel.Writer.TryWrite(batch));
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
    public async Task WaitToReadAsyncReturnsTrueOnceABatchIsAvailable()
    {
        var queue = Queue(capacity: 4);

        var waitTask = queue.WaitToReadAsync(default).AsTask();
        Assert.False(waitTask.IsCompleted);

        await queue.TryEnqueueAsync(Batch(), default);

        Assert.True(await waitTask);
    }

    [Fact]
    public async Task WaitToReadAsyncPropagatesCancellationRatherThanTimingOutSilently()
    {
        var queue = Queue(capacity: 4);

        using var cts = new CancellationTokenSource();
        var waitTask = queue.WaitToReadAsync(cts.Token).AsTask();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }

    [Fact]
    public async Task TryDequeueReadsAnAvailableBatchAndDecrementsDepth()
    {
        var queue = Queue(capacity: 4);
        await queue.TryEnqueueAsync(Batch("only"), default);

        Assert.True(await queue.WaitToReadAsync(default));
        Assert.True(queue.TryDequeue(out var batch));
        Assert.Equal("only", batch?.SourceName);
        Assert.Equal(0, queue.Depth);
    }

    [Fact]
    public void TryDequeueReturnsFalseWhenNothingIsBuffered()
    {
        var queue = Queue(capacity: 4);

        Assert.False(queue.TryDequeue(out var batch));
        Assert.Null(batch);
    }

    [Fact]
    public async Task ConcurrentProducersRacingForFreedSlotsAllSucceedEventually()
    {
        // Capacity 1 with a consumer draining continuously forces producers to
        // genuinely block in WaitToWriteAsync and contend for each freed slot —
        // the exact condition under which a single WaitToWriteAsync + TryWrite
        // attempt can lose the race and misreport saturation even though the
        // queue keeps having room. A generous timeout gives the corrected,
        // looping version space to retry instead of timing out. With capacity
        // far above the producer count (as in an earlier version of this test)
        // WaitToWriteAsync always resolves synchronously and TryWrite never
        // fails, which never touches the code path the bug lives in — so the
        // queue must stay this tight for the test to mean anything.
        const int producers = 8;
        const int batchesPerProducer = 5;
        var queue = Queue(capacity: 1, timeoutMs: 5_000);

        using var readerCts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in queue.DequeueAllAsync(readerCts.Token))
                {
                    // Drain as fast as possible so producers keep contending for
                    // freshly freed slots instead of settling into single file.
                }
            }
            catch (OperationCanceledException)
            {
                // Expected once the producers are done and the reader is stopped.
            }
        });

        var perProducerResults = await Task.WhenAll(Enumerable.Range(0, producers).Select(async _ =>
        {
            var results = new bool[batchesPerProducer];
            for (var i = 0; i < batchesPerProducer; i++)
            {
                results[i] = await queue.TryEnqueueAsync(Batch(), default);
            }

            return results;
        }));

        await readerCts.CancelAsync();
        await consumer;

        Assert.All(perProducerResults.SelectMany(r => r), Assert.True);
    }

    [Fact]
    public async Task CallerCancellationPropagatesEvenWhenTimeoutAlsoElapsed()
    {
        // The catch filter must disambiguate saturation from a caller hangup even
        // when both tokens are cancelled by the time it runs. Rather than racing
        // for that state, force it deterministically: EnqueueTimeoutMilliseconds
        // of 0 makes the timeout source cancelled from the moment it is
        // constructed, the full queue forces the call to actually wait, and the
        // caller token is already cancelled before the call starts. With both
        // flags set, a filter that only checks the timeout (the pre-fix version)
        // swallows this into `false`; the corrected filter must still propagate
        // the cancellation.
        var queue = Queue(capacity: 1, timeoutMs: 0);
        FillDirectly(queue, Batch());

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
