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

        // Minutes first: the hour aggregation reads the minute table, so the other
        // order would compute hours from minutes that are one cycle stale.
        await AdvanceAsync(
            _options.MinuteGranularityKey,
            TimeSpan.FromMinutes(1),
            _options.MaxMinuteBucketsPerCycle,
            now,
            aggregator.AggregateMinutesAsync,
            cancellationToken);

        await AdvanceAsync(
            _options.HourGranularityKey,
            TimeSpan.FromHours(1),
            _options.MaxHourBucketsPerCycle,
            now,
            aggregator.AggregateHoursAsync,
            cancellationToken);
    }

    private async Task AdvanceAsync(
        string granularityKey,
        TimeSpan bucket,
        int maxBuckets,
        DateTimeOffset now,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<int>> aggregate,
        CancellationToken cancellationToken)
    {
        // The bucket currently in progress is never written: it is not finished,
        // so any value computed for it would be wrong until it is.
        var target = Floor(now, bucket);

        var watermark = await watermarks.ReadAsync(granularityKey, cancellationToken)
                        ?? await SeedAsync(granularityKey, bucket, target, cancellationToken);

        if (watermark >= target)
        {
            return;
        }

        // Start one bucket behind so the newest completed bucket is recomputed.
        // The upsert makes that free, and it is what absorbs points that landed
        // after the bucket was first written.
        var from = watermark - (bucket * _options.TrailingBuckets);
        var to = Min(from + (bucket * (maxBuckets + _options.TrailingBuckets)), target);

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
