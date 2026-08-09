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
}
