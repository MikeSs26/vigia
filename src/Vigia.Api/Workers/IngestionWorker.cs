using System.Diagnostics;
using Microsoft.Extensions.Options;
using Vigia.Core;
using Vigia.Infrastructure.Series;
using Vigia.Infrastructure.Writing;

namespace Vigia.Api.Workers;

/// <summary>
/// Drains the queue and persists points in batches. Runs on its own cadence,
/// separate from every other worker, so that a slow rollup or a stalled notifier
/// can never apply backpressure to ingestion.
/// </summary>
public sealed class IngestionWorker(
    IMetricQueue queue,
    ISeriesResolver seriesResolver,
    ISourceResolver sourceResolver,
    IMetricWriter writer,
    IOptions<IngestionOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    private readonly IngestionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pending = new List<ResolvedPoint>(_options.MaxBatchPoints);
        var touched = new Dictionary<int, DateTimeOffset>();
        var flushInterval = TimeSpan.FromMilliseconds(_options.FlushIntervalMilliseconds);
        var sinceFlush = Stopwatch.StartNew();

        try
        {
            while (true)
            {
                // WaitToReadAsync alone would park until a batch arrives, with
                // nothing ever re-checking whether the flush interval has
                // elapsed while the queue sits idle. Bounding the wait by the
                // time remaining in the interval is what lets an idle queue
                // still flush on schedule instead of only on the next arrival
                // or on shutdown.
                if (!await TryWaitForBatchOrTimeoutAsync(sinceFlush, flushInterval, stoppingToken))
                {
                    // Timed out: nothing arrived within the interval. Flush
                    // whatever is buffered (a no-op if pending is empty) and
                    // start the next window.
                    await FlushAsync(pending, touched, stoppingToken);
                    sinceFlush.Restart();
                    continue;
                }

                if (!queue.TryDequeue(out var batch) || batch is null)
                {
                    // Lost a read race (the queue has a single reader in this
                    // host, so this is defensive rather than expected) or the
                    // queue was closed and drained between the wait and the
                    // read. Either way, nothing to process this iteration.
                    continue;
                }

                try
                {
                    await AccumulateAsync(batch, pending, touched, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One malformed batch must not take the pipeline down with it.
                    logger.LogError(ex,
                        "Failed to resolve batch for source {Source} in tenant {Tenant}",
                        batch.SourceName, batch.TenantId);
                    continue;
                }

                var full = pending.Count >= _options.MaxBatchPoints;
                var due = sinceFlush.Elapsed >= flushInterval;

                if (full || due)
                {
                    await FlushAsync(pending, touched, stoppingToken);
                    sinceFlush.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cancelled while waiting for the next item or a timeout (e.g.
            // the queue is idle when the host stops). Swallow it here rather
            // than letting it propagate, which would skip the drain below
            // and silently drop whatever was still buffered.
        }

        // Drain what is still buffered when the host shuts down.
        await FlushAsync(pending, touched, CancellationToken.None);
    }

    /// <summary>
    /// Waits for the next batch to become available, but gives up once
    /// <paramref name="flushInterval"/> has elapsed since <paramref
    /// name="sinceFlush"/> was last restarted. Returns <c>true</c> when a
    /// batch is ready to read, <c>false</c> on timeout. Propagates
    /// <see cref="OperationCanceledException"/> only for a genuine
    /// <paramref name="stoppingToken"/> cancellation, never for the internal
    /// timeout.
    /// </summary>
    private async Task<bool> TryWaitForBatchOrTimeoutAsync(
        Stopwatch sinceFlush, TimeSpan flushInterval, CancellationToken stoppingToken)
    {
        var remaining = flushInterval - sinceFlush.Elapsed;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        using var timeoutSource = new CancellationTokenSource(remaining);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            timeoutSource.Token, stoppingToken);

        try
        {
            return await queue.WaitToReadAsync(linked.Token);
        }
        catch (OperationCanceledException)
            when (timeoutSource.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // The wait ran out of time, not a caller-initiated shutdown.
            return false;
        }
    }

    private async Task AccumulateAsync(
        MetricBatch batch,
        List<ResolvedPoint> pending,
        Dictionary<int, DateTimeOffset> touched,
        CancellationToken cancellationToken)
    {
        var sourceId = await sourceResolver.ResolveAsync(
            batch.TenantId, batch.SourceName, cancellationToken);

        if (sourceId is null)
        {
            logger.LogWarning(
                "Discarding {Count} points for unregistered source {Source} in tenant {Tenant}",
                batch.Points.Count, batch.SourceName, batch.TenantId);
            return;
        }

        var latest = DateTimeOffset.MinValue;

        foreach (var point in batch.Points)
        {
            var key = new SeriesKey(
                batch.TenantId,
                sourceId.Value,
                point.Name.Value,
                point.Unit,
                SeriesKey.CanonicaliseLabels(point.Labels));

            var seriesId = await seriesResolver.ResolveAsync(key, cancellationToken);
            pending.Add(new ResolvedPoint(seriesId, point.Timestamp, point.Value));

            if (point.Timestamp > latest)
            {
                latest = point.Timestamp;
            }
        }

        touched[sourceId.Value] = latest;
    }

    private async Task FlushAsync(
        List<ResolvedPoint> pending,
        Dictionary<int, DateTimeOffset> touched,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            var written = await writer.WriteAsync(pending, cancellationToken);
            logger.LogDebug("Wrote {Count} points", written);

            foreach (var (sourceId, seenAt) in touched)
            {
                await sourceResolver.TouchAsync(sourceId, seenAt, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to write {Count} points", pending.Count);
        }
        finally
        {
            pending.Clear();
            touched.Clear();
        }
    }
}
