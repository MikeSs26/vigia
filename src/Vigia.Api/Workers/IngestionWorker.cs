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
        var sinceFlush = Stopwatch.StartNew();

        try
        {
            await foreach (var batch in queue.DequeueAllAsync(stoppingToken))
            {
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
                var due = sinceFlush.ElapsedMilliseconds >= _options.FlushIntervalMilliseconds;

                if (full || due)
                {
                    await FlushAsync(pending, touched, stoppingToken);
                    sinceFlush.Restart();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // DequeueAllAsync's ReadAllAsync throws when stoppingToken is cancelled
            // while it is waiting for the next item (e.g. the queue is idle when the
            // host stops). Swallow it here rather than letting it propagate past the
            // loop, which would skip the drain below and silently drop whatever was
            // still buffered.
        }

        // Drain what is still buffered when the host shuts down.
        await FlushAsync(pending, touched, CancellationToken.None);
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
