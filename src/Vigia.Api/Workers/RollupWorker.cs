using Microsoft.Extensions.Options;
using Vigia.Infrastructure.Rollups;

namespace Vigia.Api.Workers;

/// <summary>
/// Aggregates raw points into 1-minute buckets and those into 1-hour buckets,
/// tracking how far it has got in a persisted watermark. Raw points expire after
/// seven days, so this worker is the only thing standing between the system and
/// forgetting everything it has ever measured.
/// </summary>
public sealed class RollupWorker(
    IRollupAggregator aggregator,
    IRollupWatermarkStore watermarks,
    IOptions<RollupOptions> options,
    TimeProvider timeProvider,
    ILogger<RollupWorker> logger) : BackgroundService
{
    private readonly RollupOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.IntervalSeconds), timeProvider);

        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One failed cycle must not end the worker: the watermark only
                // advances after a successful write, so the next tick re-covers
                // exactly the range this one did not.
                logger.LogError(ex, "Rollup cycle failed; will retry on the next tick");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var hour = TimeSpan.FromHours(1);

        // Minutes first: the hour aggregation reads the minute table, so the other
        // order would compute hours from minutes that are one cycle stale. Both
        // targets are floored because the bucket currently in progress is never
        // written: it is not finished, so any value computed for it would be wrong.
        var minuteWatermark = await AdvanceAsync(
            _options.MinuteGranularityKey,
            TimeSpan.FromMinutes(1),
            _options.MaxMinuteBucketsPerCycle,
            _options.TrailingMinuteBuckets,
            Floor(now, TimeSpan.FromMinutes(1)),
            now,
            aggregator.AggregateMinutesAsync,
            cancellationToken);

        // Hours are computed FROM the minute table, and the two passes are capped
        // at very different rates: 1,440 minutes against 720 hours. Clamping the
        // hour target to the minutes that actually exist is what stops the hour
        // pass, on any backlog longer than its minute counterpart can cover in one
        // cycle, from reading hours the minute pass has not written, storing the
        // fraction it finds, and marking them complete forever.
        await AdvanceAsync(
            _options.HourGranularityKey,
            hour,
            _options.MaxHourBucketsPerCycle,
            _options.TrailingHourBuckets,
            Min(Floor(now, hour), Floor(minuteWatermark, hour)),
            now,
            aggregator.AggregateHoursAsync,
            cancellationToken);
    }

    private async Task<DateTimeOffset> AdvanceAsync(
        string granularityKey,
        TimeSpan bucket,
        int maxBuckets,
        int trailingBuckets,
        DateTimeOffset target,
        DateTimeOffset now,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<int>> aggregate,
        CancellationToken cancellationToken)
    {
        var watermark = await watermarks.ReadAsync(granularityKey, cancellationToken)
                        ?? await SeedAsync(granularityKey, bucket, target, cancellationToken);

        if (watermark >= target)
        {
            return watermark;
        }

        // Start a window behind so already-written buckets are recomputed. The
        // upsert makes that free in correctness, and it is what absorbs points
        // that committed after their bucket was first written — the spool replays
        // batches long after the measurements in them were taken.
        var from = watermark - (bucket * trailingBuckets);
        var to = Min(from + (bucket * (maxBuckets + trailingBuckets)), target);

        var written = await aggregate(from, to, cancellationToken);

        // Only after the write commits. A crash before this line re-covers the
        // range next cycle; a crash after it has already persisted the rows.
        await watermarks.WriteAsync(granularityKey, to, now, cancellationToken);

        if (written > 0)
        {
            logger.LogInformation(
                "Rolled up {Rows} {Granularity} buckets covering {From:o} to {To:o}",
                written, granularityKey, from, to);
        }

        return to;
    }

    private async Task<DateTimeOffset> SeedAsync(
        string granularityKey,
        TimeSpan bucket,
        DateTimeOffset target,
        CancellationToken cancellationToken)
    {
        // Cold start: begin at the oldest raw point rather than at now, so history
        // written before this worker existed is aggregated rather than skipped.
        var oldest = await aggregator.OldestRawTimestampAsync(cancellationToken);
        var seed = oldest is null ? target : Floor(oldest.Value, bucket);

        logger.LogInformation(
            "Seeding the {Granularity} watermark at {Seed:o}", granularityKey, seed);

        return seed;
    }

    private static DateTimeOffset Floor(DateTimeOffset value, TimeSpan bucket) =>
        new(value.UtcTicks - (value.UtcTicks % bucket.Ticks), TimeSpan.Zero);

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
