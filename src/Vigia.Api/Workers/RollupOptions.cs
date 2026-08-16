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
    /// Buckets recomputed behind the watermark on every cycle. One is enough to
    /// absorb points that arrive while a bucket is being computed; the agent's
    /// spool drains oldest-first, so a delayed batch lands seconds late, not days.
    /// </summary>
    public int TrailingBuckets { get; init; } = 1;

    /// <summary>Row key in rollup_watermarks. Overridable so tests can isolate.</summary>
    public string MinuteGranularityKey { get; init; } = "1m";

    public string HourGranularityKey { get; init; } = "1h";
}
