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
            // Loop rather than wait once. WaitToWriteAsync returning true only means
            // a slot was free at that moment, and SingleWriter is false, so two
            // producers can be woken for the same slot with one losing the race.
            // Treating a lost race as saturation would shed a request the queue had
            // room for.
            while (await _channel.Writer.WaitToWriteAsync(linked.Token))
            {
                if (_channel.Writer.TryWrite(batch))
                {
                    Interlocked.Increment(ref _depth);
                    return true;
                }
            }

            // WaitToWriteAsync returned false: the writer is completed and nothing
            // will ever be accepted again.
            return false;
        }
        catch (OperationCanceledException)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Saturated: the wait ran out of time. The second condition keeps this
            // distinct from a caller-initiated cancellation, which must propagate.
            // One means "shed this request with 429", the other means "the client
            // hung up" — collapsing them would report a hangup as backpressure.
            return false;
        }
    }

    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken) =>
        _channel.Reader.WaitToReadAsync(cancellationToken);

    public bool TryDequeue(out MetricBatch? batch)
    {
        if (_channel.Reader.TryRead(out var item))
        {
            Interlocked.Decrement(ref _depth);
            batch = item;
            return true;
        }

        batch = null;
        return false;
    }
}
