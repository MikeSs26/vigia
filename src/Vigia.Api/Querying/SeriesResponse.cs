namespace Vigia.Api.Querying;

public sealed record SeriesResponse(
    string Source,
    string Name,
    string Granularity,
    string Aggregation,
    IReadOnlyList<SeriesPayload> Series);

public sealed record SeriesPayload(
    string Unit,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyList<PointPayload> Points);

public sealed record PointPayload(DateTimeOffset Ts, double Value);
