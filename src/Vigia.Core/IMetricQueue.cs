namespace Vigia.Core;

/// <summary>
/// The seam between accepting a request and persisting it. Declared here, in the
/// dependency-free project, so that replacing the in-process channel with an
/// external broker later means adding an implementation rather than reworking
/// the pipeline.
/// </summary>
public interface IMetricQueue
{
    /// <summary>
    /// Enqueues a batch. Returns <c>false</c> when the queue is saturated, which
    /// the caller must surface as a rejection rather than retrying in place.
    /// </summary>
    ValueTask<bool> TryEnqueueAsync(MetricBatch batch, CancellationToken cancellationToken);

    /// <summary>
    /// Waits until a batch is available to read or the queue is closed and
    /// drained. Returns <c>true</c> when a batch is ready — retrieve it with
    /// <see cref="TryDequeue"/> — or <c>false</c> once the queue is closed
    /// and empty and will never yield another batch.
    ///
    /// Exists for consumers that need to bound how long they wait for the
    /// next item, e.g. to flush an already-accumulated batch on a timer even
    /// when no new data arrives: pass a token that is cancelled after the
    /// desired timeout and race it against this call, distinguishing a
    /// timeout from a genuine shutdown by which token fired.
    /// </summary>
    ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Synchronous, non-blocking attempt to dequeue one batch. Returns
    /// <c>false</c> with <paramref name="batch"/> set to <c>null</c> when
    /// nothing is currently available — including the case where another
    /// reader won a race for the only available item.
    /// </summary>
    bool TryDequeue(out MetricBatch? batch);

    int Depth { get; }
}
