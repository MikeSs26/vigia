using Vigia.Core.PublicStatus;

namespace Vigia.Infrastructure.PublicStatus;

/// <param name="TenantId">
/// The single tenant the public page describes. Scoping it to one is what stops
/// an unauthenticated page becoming a directory of everything in the database.
/// </param>
/// <param name="MetricNames">The handful of metrics worth showing, in display order.</param>
/// <param name="StaleAfter">A source silent for longer than this is reported as down.</param>
/// <param name="IncidentLimit">Upper bound on the events read to build the history.</param>
public readonly record struct PublicStatusQuery(
    int TenantId,
    IReadOnlyList<string> MetricNames,
    TimeSpan StaleAfter,
    int IncidentLimit);

public interface IPublicStatusReader
{
    Task<PublicStatusSnapshot> ReadAsync(
        PublicStatusQuery query, DateTimeOffset now, CancellationToken cancellationToken);
}
