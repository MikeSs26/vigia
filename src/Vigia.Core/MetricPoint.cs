namespace Vigia.Core;

/// <summary>One sample. <paramref name="Labels"/> is null for the common case.</summary>
public sealed record MetricPoint(
    MetricName Name,
    string Unit,
    DateTimeOffset Timestamp,
    double Value,
    IReadOnlyDictionary<string, string>? Labels = null);
