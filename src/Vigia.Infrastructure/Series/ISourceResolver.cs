namespace Vigia.Infrastructure.Series;

public interface ISourceResolver
{
    /// <summary>Returns the source id, or null when the tenant has no such source.</summary>
    Task<int?> ResolveAsync(int tenantId, string sourceName, CancellationToken cancellationToken);

    Task TouchAsync(int sourceId, DateTimeOffset seenAt, CancellationToken cancellationToken);
}
