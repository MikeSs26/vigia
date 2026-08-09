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
/// </summary>
public sealed class SeriesResolver(string connectionString, int cacheCapacity = 10_000) : ISeriesResolver
{
    private readonly ConcurrentDictionary<SeriesKey, int> _cache = new();

    public int CachedCount => _cache.Count;

    public async Task<int> ResolveAsync(SeriesKey key, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var id = await UpsertAsync(key, cancellationToken);

        if (_cache.Count >= cacheCapacity)
        {
            Evict();
        }

        _cache[key] = id;
        return id;
    }

    /// <summary>
    /// One statement, not a SELECT followed by an INSERT: sixteen concurrent
    /// writers must produce one row, and the unique constraint plus ON CONFLICT
    /// is what guarantees that without a lock held in application code.
    /// </summary>
    private async Task<int> UpsertAsync(SeriesKey key, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            WITH inserted AS (
                INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
                VALUES (@tenant, @source, @name, @unit, @labels::jsonb)
                ON CONFLICT (tenant_id, source_id, name, labels) DO NOTHING
                RETURNING id
            )
            SELECT id FROM inserted
            UNION ALL
            SELECT id FROM metric_series
            WHERE tenant_id = @tenant AND source_id = @source
              AND name = @name AND labels = @labels::jsonb
            LIMIT 1;
            """, connection);

        command.Parameters.AddWithValue("tenant", key.TenantId);
        command.Parameters.AddWithValue("source", key.SourceId);
        command.Parameters.AddWithValue("name", key.Name);
        command.Parameters.AddWithValue("unit", key.Unit);
        command.Parameters.Add(new NpgsqlParameter("labels", NpgsqlDbType.Text)
        {
            Value = key.CanonicalLabels,
        });

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    /// <summary>
    /// Drops a quarter of the entries. Not LRU: tracking recency would need a
    /// lock on every hit, and the access pattern here is a small set of series
    /// read repeatedly, so any eviction refills almost immediately.
    /// </summary>
    private void Evict()
    {
        var target = Math.Max(1, cacheCapacity / 4);
        var removed = 0;

        foreach (var entry in _cache)
        {
            if (removed >= target)
            {
                break;
            }

            if (_cache.TryRemove(entry.Key, out _))
            {
                removed++;
            }
        }
    }
}
