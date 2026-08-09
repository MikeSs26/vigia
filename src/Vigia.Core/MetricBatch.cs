namespace Vigia.Core;

/// <summary>
/// What the ingest endpoint enqueues. The tenant comes from the authenticated
/// API key, never from the request body.
/// </summary>
public sealed record MetricBatch(
    int TenantId,
    string SourceName,
    IReadOnlyList<MetricPoint> Points);
