using System.Collections.Concurrent;
using Npgsql;

namespace Vigia.Infrastructure.Series;

/// <summary>
/// Sources are created explicitly through the CLI, never implicitly by ingest.
/// Auto-creating them would let a single leaked ingest key fill the table with
/// arbitrary rows, and would turn a typo in an agent's configuration into a new
/// source that silently looks healthy.
/// </summary>
public sealed class SourceResolver(string connectionString) : ISourceResolver
{
    private readonly ConcurrentDictionary<(int TenantId, string Name), int> _cache = new();

    public async Task<int?> ResolveAsync(
        int tenantId, string sourceName, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue((tenantId, sourceName), out var cached))
        {
            return cached;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT id FROM sources WHERE tenant_id = @t AND name = @n;", connection);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("n", sourceName);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            return null;
        }

        var id = Convert.ToInt32(result);
        _cache[(tenantId, sourceName)] = id;
        return id;
    }

    public async Task TouchAsync(
        int sourceId, DateTimeOffset seenAt, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "UPDATE sources SET last_seen_at = @s WHERE id = @id;", connection);
        // Npgsql refuses to write a non-zero offset to timestamptz. Nothing
        // upstream should ever pass one in, but normalising here too means this
        // call cannot throw on that specific cause regardless of what a future
        // caller passes.
        command.Parameters.AddWithValue("s", seenAt.ToUniversalTime());
        command.Parameters.AddWithValue("id", sourceId);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
