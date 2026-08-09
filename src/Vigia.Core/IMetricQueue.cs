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

    IAsyncEnumerable<MetricBatch> DequeueAllAsync(CancellationToken cancellationToken);

    int Depth { get; }
}
