using Vigia.Core.Querying;

namespace Vigia.Infrastructure.Querying;

public readonly record struct MetricQuery(
    int TenantId,
    int SourceId,
    string Name,
    DateTimeOffset From,
    DateTimeOffset To,
    Granularity Granularity,
    Aggregation Aggregation);

public readonly record struct SeriesPoint(DateTimeOffset Ts, double Value);

public sealed record SeriesResult(
    string Unit,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<SeriesPoint> Points);

public interface IMetricQueryReader
{
    Task<IReadOnlyList<SeriesResult>> ReadAsync(
        MetricQuery query, CancellationToken cancellationToken);
}
