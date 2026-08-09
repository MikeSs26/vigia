using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Vigia.Core;

namespace Vigia.Api.Queue;

public sealed class BoundedChannelMetricQueue : IMetricQueue
{
    private readonly Channel<MetricBatch> _channel;
    private readonly int _enqueueTimeoutMilliseconds;
    private int _depth;

    public BoundedChannelMetricQueue(IOptions<QueueOptions> options)
    {
        var settings = options.Value;
        _enqueueTimeoutMilliseconds = settings.EnqueueTimeoutMilliseconds;

        _channel = Channel.CreateBounded<MetricBatch>(new BoundedChannelOptions(settings.Capacity)
        {
            // Wait, not DropWrite: silently discarding telemetry would leave a gap
            // that looks identical to a healthy quiet period. A rejection is visible.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public int Depth => Volatile.Read(ref _depth);

    public async ValueTask<bool> TryEnqueueAsync(
        MetricBatch batch, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_enqueueTimeoutMilliseconds);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeout.Token, cancellationToken);

        try
        {
            await _channel.Writer.WaitToWriteAsync(linked.Token);

            if (!_channel.Writer.TryWrite(batch))
            {
                return false;
            }

            Interlocked.Increment(ref _depth);
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return false;
        }
    }

    public async IAsyncEnumerable<MetricBatch> DequeueAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await foreach (var batch in _channel.Reader.ReadAllAsync(cancellationToken))
        {
            Interlocked.Decrement(ref _depth);
            yield return batch;
        }
    }
}
