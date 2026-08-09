namespace Vigia.Infrastructure.Partitions;

/// <summary>
/// The single place in the codebase permitted to know that the time-series
/// tables are physically partitioned, or to construct a partition name. Keeping
/// that knowledge here is what makes adopting a time-series extension a matter
/// of replacing this implementation and nothing else.
/// </summary>
public interface IPartitionMaintenance
{
    /// <summary>Creates weekly partitions covering <paramref name="weeksAhead"/> weeks
    /// starting at the week containing <paramref name="from"/>. Returns the names created;
    /// partitions that already exist are skipped.</summary>
    Task<IReadOnlyList<string>> EnsurePartitionsAsync(
        string table, DateTimeOffset from, int weeksAhead, CancellationToken cancellationToken);

    /// <summary>Drops partitions whose entire range is older than
    /// <paramref name="olderThan"/>. Returns the names dropped.</summary>
    Task<IReadOnlyList<string>> DropExpiredAsync(
        string table, DateTimeOffset olderThan, CancellationToken cancellationToken);
}
