namespace Vigia.Infrastructure.Rollups;

/// <summary>
/// Runs one aggregation over a half-open range. Every statement is an idempotent
/// upsert, so recomputing a range is indistinguishable from computing it once —
/// which is what lets the worker advance its watermark only after the write
/// commits, and re-cover a range after a crash without corrupting anything.
/// </summary>
public interface IRollupAggregator
{
    Task<int> AggregateMinutesAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    Task<int> AggregateHoursAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Oldest raw point present, used to seed a cold-start watermark.</summary>
    Task<DateTimeOffset?> OldestRawTimestampAsync(CancellationToken cancellationToken);
}
