namespace Vigia.Infrastructure.Rollups;

/// <summary>
/// Remembers how far each granularity has been aggregated. The watermark is the
/// first bucket NOT yet known to be complete, so a cold start and a restart after
/// an outage are the same operation: aggregate from the watermark forward.
/// </summary>
public interface IRollupWatermarkStore
{
    Task<DateTimeOffset?> ReadAsync(string granularity, CancellationToken cancellationToken);

    Task WriteAsync(
        string granularity,
        DateTimeOffset watermark,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken);
}
