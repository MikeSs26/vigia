namespace Vigia.Api.Workers;

public sealed class MaintenanceOptions
{
    public const string SectionName = "Maintenance";

    /// <summary>
    /// How many weekly partitions to keep ahead of now. Two is the floor: with
    /// one, a single missed cycle near a week boundary makes inserts fail.
    /// </summary>
    public int WeeksAhead { get; init; } = 3;

    /// <summary>
    /// How many weekly partitions to keep BEHIND now on the rollup tables. The
    /// rollup worker seeds a cold-start watermark at the oldest raw point, which
    /// can be nearly two weeks old by the time a raw partition expires, and it
    /// recomputes a trailing window behind that. Three weeks covers both with
    /// margin. The raw table derives its own reach from the retention horizon
    /// instead, since that is how far back ingestion will accept a timestamp.
    /// </summary>
    public int RollupWeeksBehind { get; init; } = 3;

    public int RawRetentionDays { get; init; } = 7;

    /// <summary>1-minute rollups outlive raw points; 30 days is ~43k buckets per series.</summary>
    public int MinuteRollupRetentionDays { get; init; } = 30;

    /// <summary>A year of hourly data is 8,760 buckets per series, which the query cap accommodates.</summary>
    public int HourRollupRetentionDays { get; init; } = 365;

    public int IntervalMinutes { get; init; } = 60;
}
