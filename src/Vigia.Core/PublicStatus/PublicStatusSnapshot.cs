namespace Vigia.Core.PublicStatus;

/// <summary>
/// Everything the public status page shows, and deliberately nothing else.
///
/// Names travel, identifiers do not: a page anyone can read has no business
/// disclosing tenant, source or rule ids, and nothing here can be used to ask a
/// different question than the one the page answers.
/// </summary>
public sealed record PublicStatusSnapshot(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PublicSourceStatus> Sources,
    IReadOnlyList<PublicIncident> Incidents);

public sealed record PublicSourceStatus(
    string Name,
    bool IsUp,
    DateTimeOffset? LastSeenAt,
    IReadOnlyList<PublicMetric> Metrics);

public readonly record struct PublicMetric(string Name, double Value, string Unit);

/// <summary>
/// One period during which a rule was not in its healthy state.
/// <paramref name="ResolvedAt"/> is null while it is still going on.
/// </summary>
public sealed record PublicIncident(
    string SourceName,
    string MetricName,
    IncidentKind Kind,
    DateTimeOffset StartedAt,
    DateTimeOffset? ResolvedAt)
{
    public bool IsOngoing => ResolvedAt is null;

    public TimeSpan? Duration => ResolvedAt is { } resolved ? resolved - StartedAt : null;
}

public enum IncidentKind
{
    /// <summary>A threshold was breached and held.</summary>
    Firing,

    /// <summary>The source stopped reporting altogether.</summary>
    NoData,
}
