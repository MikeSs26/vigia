namespace Vigia.Api.Workers;

public sealed class RollupOptions
{
    public const string SectionName = "Rollup";

    public int IntervalSeconds { get; init; } = 60;

    /// <summary>
    /// Upper bound on how many buckets one cycle may cover. Without it, the first
    /// cycle after a long gap would attempt one statement over the whole backlog
    /// and be killed partway on a small host; with it, the worker converges over
    /// several cycles and every statement stays small.
    /// </summary>
    public int MaxMinuteBucketsPerCycle { get; init; } = 1440;

    public int MaxHourBucketsPerCycle { get; init; } = 720;

    /// <summary>
    /// Minute buckets recomputed behind the watermark on every cycle. This is what
    /// absorbs points that commit after their bucket was first written. The agent
    /// spools through an outage and replays one batch per tick, so a point can
    /// arrive an hour or more after it was measured; anything landing further
    /// behind than this window never enters the rollups at all and is lost when
    /// its raw partition expires. Two hours covers a realistic outage and costs
    /// one extra scan of a small, recent, BRIN-indexed range per cycle. The upsert
    /// is idempotent, so recomputing is free in correctness — only in CPU.
    /// </summary>
    public int TrailingMinuteBuckets { get; init; } = 120;

    /// <summary>
    /// Hour buckets recomputed per cycle, which is how corrections to the minute
    /// table propagate into the hourly archive. Must cover the minute window above.
    /// </summary>
    public int TrailingHourBuckets { get; init; } = 3;

    /// <summary>Row key in rollup_watermarks. Overridable so tests can isolate.</summary>
    public string MinuteGranularityKey { get; init; } = "1m";

    public string HourGranularityKey { get; init; } = "1h";
}
