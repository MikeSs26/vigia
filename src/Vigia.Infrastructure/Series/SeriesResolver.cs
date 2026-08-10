using System.Collections.Concurrent;
using Npgsql;
using NpgsqlTypes;
using Vigia.Core;

namespace Vigia.Infrastructure.Series;

/// <summary>
/// Maps a series identity to its integer id. This sits on the ingest hot path, so
/// a database round trip per point is not acceptable; resolved ids are cached.
///
/// The cache is bounded. An unbounded cache turns a cardinality mistake by any
/// client into unbounded memory growth in the server, which is a denial of
/// service the server inflicted on itself.
///
/// Bound guarantee: cache-hit lookups never take a lock. Insertions may briefly
/// push the cache over <see cref="cacheCapacity"/> while several threads resolve
/// distinct new keys concurrently; eviction then brings the count back down to
/// capacity under a lock. This is a transient overshoot bounded by the number of
/// concurrent inserters, converging to capacity — not a hard, instantaneous bound.
/// </summary>
public sealed class SeriesResolver(string connectionString, int cacheCapacity = 10_000) : ISeriesResolver
{
    /// <summary>
    /// Bounded retries for the insert/select pair below. A retry is only needed
    /// when the row a losing INSERT would have seen gets deleted between our
    /// INSERT attempt and our fallback SELECT — vanishingly rare, but the loop
    /// must be bounded rather than infinite.
    /// </summary>
    private const int MaxUpsertAttempts = 5;

    private readonly ConcurrentDictionary<SeriesKey, int> _cache = new();
    private readonly object _evictionLock = new();

    public int CachedCount => _cache.Count;

    public async Task<int> ResolveAsync(SeriesKey key, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var id = await UpsertAsync(key, cancellationToken);

        if (id <= 0)
        {
            // Defensive backstop: UpsertAsync should already throw rather than
            // return a non-positive id, but the cache must never trust a
            // fabricated id even if that invariant is ever violated.
            throw new InvalidOperationException(
                $"Series resolution produced a non-positive id ({id}) for tenant {key.TenantId}, " +
                $"source {key.SourceId}, name '{key.Name}', labels {key.CanonicalLabels}. Refusing to cache it.");
        }

        _cache[key] = id;

        if (_cache.Count > cacheCapacity)
        {
            EvictToCapacity();
        }

        return id;
    }

    /// <summary>
    /// Resolves the id for <paramref name="key"/> by inserting it if absent.
    ///
    /// This is deliberately NOT a single "INSERT ... ON CONFLICT DO NOTHING
    /// RETURNING id UNION ALL SELECT ..." statement. Under Read Committed, a CTE
    /// and every arm of a UNION ALL in the same statement share one MVCC
    /// snapshot taken at statement start. When two connections race for the same
    /// identity, the loser's INSERT blocks on the winner's speculative insert;
    /// once the winner commits, ON CONFLICT DO NOTHING correctly finds the
    /// conflict (that check reads the latest committed data, not the snapshot)
    /// and returns zero rows — but the fallback SELECT in the same statement
    /// still runs against the loser's original pre-commit snapshot and cannot
    /// see the winner's newly committed row either. Both arms return nothing,
    /// and callers that aren't careful can end up treating that as id 0.
    ///
    /// Splitting the insert and the fallback select into separate statements
    /// gives the fallback select a fresh snapshot (each statement in autocommit
    /// mode is its own implicit transaction under Read Committed), so it does
    /// see the winner's committed row.
    /// </summary>
    private async Task<int> UpsertAsync(SeriesKey key, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        for (var attempt = 0; attempt < MaxUpsertAttempts; attempt++)
        {
            var insertedId = await TryInsertAsync(connection, key, cancellationToken);
            if (insertedId is int inserted)
            {
                return inserted;
            }

            var existingId = await TrySelectAsync(connection, key, cancellationToken);
            if (existingId is int existing)
            {
                return existing;
            }

            // Neither the insert nor the fallback select found a row. The row
            // that caused our INSERT to conflict must have been deleted between
            // the two statements. Retry the pair rather than fabricate an id.
        }

        throw new InvalidOperationException(
            $"Could not resolve a series id for tenant {key.TenantId}, source {key.SourceId}, " +
            $"name '{key.Name}', labels {key.CanonicalLabels} after {MaxUpsertAttempts} attempts.");
    }

    private static async Task<int?> TryInsertAsync(
        NpgsqlConnection connection, SeriesKey key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
            VALUES (@tenant, @source, @name, @unit, @labels::jsonb)
            ON CONFLICT (tenant_id, source_id, name, labels) DO NOTHING
            RETURNING id;
            """, connection);

        AddParameters(command, key);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int id ? id : null;
    }

    private static async Task<int?> TrySelectAsync(
        NpgsqlConnection connection, SeriesKey key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT id FROM metric_series
            WHERE tenant_id = @tenant AND source_id = @source
              AND name = @name AND labels = @labels::jsonb
            LIMIT 1;
            """, connection);

        AddParameters(command, key);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is int id ? id : null;
    }

    private static void AddParameters(NpgsqlCommand command, SeriesKey key)
    {
        command.Parameters.AddWithValue("tenant", key.TenantId);
        command.Parameters.AddWithValue("source", key.SourceId);
        command.Parameters.AddWithValue("name", key.Name);
        command.Parameters.AddWithValue("unit", key.Unit);
        command.Parameters.Add(new NpgsqlParameter("labels", NpgsqlDbType.Text)
        {
            Value = key.CanonicalLabels,
        });
    }

    /// <summary>
    /// Brings the cache back down to <see cref="cacheCapacity"/>. Not LRU:
    /// tracking recency would need a lock on every hit, and the access pattern
    /// here is a small set of series read repeatedly, so any eviction refills
    /// almost immediately.
    ///
    /// Only the (cold) insert path takes this lock; the (hot) cache-hit path in
    /// <see cref="ResolveAsync"/> stays lock-free. Because insertion happens
    /// before this check, several concurrent inserters can each push the count
    /// past capacity before any of them evicts — the outer loop re-checks the
    /// count after each pass so the cache still converges to capacity rather
    /// than drifting upward indefinitely.
    /// </summary>
    private void EvictToCapacity()
    {
        lock (_evictionLock)
        {
            while (_cache.Count > cacheCapacity)
            {
                foreach (var entry in _cache)
                {
                    if (_cache.Count <= cacheCapacity)
                    {
                        break;
                    }

                    _cache.TryRemove(entry.Key, out _);
                }
            }
        }
    }
}
