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
    ILogger<IngestionWorker> logger,
    IIngestionMetrics metrics) : BackgroundService
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
                    //
                    // CancellationToken.None, not stoppingToken: a shutdown that
                    // lands while this flush's COPY is already in flight must
                    // not abort it. A batch is at most MaxBatchPoints rows —
                    // short — and the api service sets stop_grace_period: 30s
                    // (deploy/docker-compose.yml), so letting it finish is
                    // cheap. That grace is set explicitly rather than left to
                    // Docker's 10 second default precisely because this
                    // decision leans on it. Passing
                    // stoppingToken here was the entire bug: it let shutdown
                    // cancel an in-progress write, and the resulting
                    // OperationCanceledException fell through FlushAsync's catch
                    // filter into its `finally`, which cleared `pending`
                    // regardless — so the shutdown drain below found nothing
                    // left to persist, and nothing was ever logged.
                    await FlushAsync(pending, touched, CancellationToken.None);
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
                    // See the timeout branch above for why this is
                    // CancellationToken.None rather than stoppingToken.
                    await FlushAsync(pending, touched, CancellationToken.None);
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

        await DrainOnShutdownAsync(pending, touched);
    }

    /// <summary>
    /// Persists everything the host still owes on shutdown: the points already
    /// accumulated in <paramref name="pending"/>, and — the part that was
    /// missing — the batches still sitting in the queue, unread.
    ///
    /// The loop above cannot drain those itself. ChannelReader.WaitToReadAsync
    /// checks its cancellation token before checking for available items, so it
    /// throws the instant the host stops even against a completely full queue;
    /// the loop exits and nothing reads the channel again. Everything left
    /// there had already been answered 202, so dropping it is the worst failure
    /// this system can have: accepted data lost without a trace.
    ///
    /// Bounded by <see cref="IngestionOptions.ShutdownDrainMilliseconds"/> so
    /// keeping that promise cannot hang shutdown — and if the budget expires
    /// with batches still queued, the loss is counted and logged rather than
    /// repeated silently. Silence is the thing being fixed here; an
    /// unavoidable discard still has to be visible.
    ///
    /// Every database call uses CancellationToken.None, consistently with the
    /// in-loop flushes: this whole method runs after stoppingToken has already
    /// been cancelled, so any other token would abort it immediately.
    /// </summary>
    private async Task DrainOnShutdownAsync(
        List<ResolvedPoint> pending, Dictionary<int, DateTimeOffset> touched)
    {
        var budget = TimeSpan.FromMilliseconds(_options.ShutdownDrainMilliseconds);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < budget && queue.TryDequeue(out var batch) && batch is not null)
        {
            try
            {
                await AccumulateAsync(batch, pending, touched, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Same isolation as the main loop: one bad batch must not cost
                // us the rest of the drain.
                logger.LogError(ex,
                    "Failed to resolve batch for source {Source} in tenant {Tenant} " +
                    "during the shutdown drain",
                    batch.SourceName, batch.TenantId);
                continue;
            }

            if (pending.Count >= _options.MaxBatchPoints)
            {
                await FlushAsync(pending, touched, CancellationToken.None);
            }
        }

        await FlushAsync(pending, touched, CancellationToken.None);

        // Past the budget (or the writer failed): whatever is left is going to
        // be lost, so count it exactly. Draining the remainder here is pure
        // in-memory work — no database, no further waiting — which is what
        // makes an exact point count affordable at this stage.
        var discarded = 0;
        var discardedBatches = 0;
        while (queue.TryDequeue(out var leftover) && leftover is not null)
        {
            discarded += leftover.Points.Count;
            discardedBatches++;
        }

        if (discarded > 0)
        {
            logger.LogError(
                "Shutdown drain budget of {BudgetMs}ms expired with batches still queued: " +
                "discarding {Count} points across {BatchCount} batches that were already " +
                "accepted with 202 and will never be persisted",
                _options.ShutdownDrainMilliseconds, discarded, discardedBatches);

            metrics.RecordDropped(discarded, "shutdown_drain_timeout");
        }
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
            // This failure is not attributable to specific rows — the writer
            // already isolates and normalises the one row-level failure this
            // pipeline can identify in advance (a non-UTC timestamp offset), so
            // whatever reaches here is something else entirely: a dropped
            // connection, a timestamp outside every partition that exists, and
            // so on. There is no reliable way to tell from a COPY failure which
            // rows if any were "the" problem, so the whole window is discarded.
            // What can be done is make that loss loud and actionable: identify
            // the discarded window precisely, and count it so it is visible to
            // anything monitoring the process, not just to whoever reads this
            // log line.
            var minTimestamp = pending[0].Timestamp;
            var maxTimestamp = pending[0].Timestamp;
            var seriesIds = new HashSet<int>();
            foreach (var point in pending)
            {
                if (point.Timestamp < minTimestamp)
                {
                    minTimestamp = point.Timestamp;
                }

                if (point.Timestamp > maxTimestamp)
                {
                    maxTimestamp = point.Timestamp;
                }

                seriesIds.Add(point.SeriesId);
            }

            logger.LogError(ex,
                "Failed to write {Count} points spanning {MinTimestamp:o} to {MaxTimestamp:o} " +
                "across {SeriesCount} series (ids: {SeriesIds}); the whole window is being " +
                "discarded because the failure cannot be attributed to specific rows",
                pending.Count, minTimestamp, maxTimestamp, seriesIds.Count,
                string.Join(",", seriesIds.Take(20)));

            metrics.RecordDropped(pending.Count, "flush_failure");
        }
        finally
        {
            pending.Clear();
            touched.Clear();
        }
    }
}
