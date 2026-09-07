using Microsoft.Extensions.Options;
using Vigia.Core.PublicStatus;
using Vigia.Infrastructure.PublicStatus;

namespace Vigia.Api.PublicStatus;

/// <summary>
/// Serves one built snapshot for a few seconds rather than rebuilding it per
/// request.
///
/// This is the load-bearing piece of making the page public. Without it, anyone
/// holding refresh reaches PostgreSQL as fast as they can type, on the same 1 GB
/// host that runs the database. The semaphore matters as much as the cache: when
/// an expired entry is hit by several requests at once, exactly one of them
/// rebuilds and the rest wait for it, so a cold cache under load produces one
/// query rather than a thundering herd.
/// </summary>
public sealed class PublicStatusSnapshotCache(
    IPublicStatusReader reader,
    IOptions<PublicStatusOptions> options,
    TimeProvider timeProvider)
{
    private readonly PublicStatusOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PublicStatusSnapshot? _snapshot;
    private DateTimeOffset _builtAt;

    public async Task<PublicStatusSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        if (IsFresh(now))
        {
            return _snapshot!;
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            // Someone may have rebuilt it while this request waited for the gate.
            now = timeProvider.GetUtcNow();

            if (IsFresh(now))
            {
                return _snapshot!;
            }

            _snapshot = await reader.ReadAsync(
                new PublicStatusQuery(
                    _options.TenantId,
                    _options.Metrics,
                    TimeSpan.FromSeconds(_options.StaleAfterSeconds),
                    _options.IncidentLimit),
                now,
                cancellationToken);

            _builtAt = now;

            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(DateTimeOffset now) =>
        _snapshot is not null
        && now - _builtAt < TimeSpan.FromSeconds(_options.CacheSeconds);
}
