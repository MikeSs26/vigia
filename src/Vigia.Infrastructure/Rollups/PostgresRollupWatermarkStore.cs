using Npgsql;

namespace Vigia.Infrastructure.Rollups;

public sealed class PostgresRollupWatermarkStore(string connectionString) : IRollupWatermarkStore
{
    public async Task<DateTimeOffset?> ReadAsync(
        string granularity, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            "SELECT watermark FROM rollup_watermarks WHERE granularity = @g;", connection);
        command.Parameters.AddWithValue("g", granularity);

        var result = await command.ExecuteScalarAsync(cancellationToken);

        // Npgsql maps timestamptz to a DateTime with Kind=Utc, not to a
        // DateTimeOffset. Wrapping it here keeps that mapping detail from leaking
        // into every caller, and the zero offset is guaranteed by the Kind.
        return result is null or DBNull ? null : new DateTimeOffset((DateTime)result);
    }

    public async Task WriteAsync(
        string granularity,
        DateTimeOffset watermark,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            """
            INSERT INTO rollup_watermarks (granularity, watermark, updated_at)
            VALUES (@g, @w, @u)
            ON CONFLICT (granularity) DO UPDATE
            SET watermark = excluded.watermark, updated_at = excluded.updated_at;
            """, connection);

        command.Parameters.AddWithValue("g", granularity);
        // Npgsql refuses a non-zero offset on timestamptz; normalise at the boundary
        // rather than trusting every caller to remember.
        command.Parameters.AddWithValue("w", watermark.ToUniversalTime());
        command.Parameters.AddWithValue("u", updatedAt.ToUniversalTime());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
