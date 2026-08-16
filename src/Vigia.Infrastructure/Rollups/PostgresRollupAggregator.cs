using Npgsql;

namespace Vigia.Infrastructure.Rollups;

public sealed class PostgresRollupAggregator(string connectionString) : IRollupAggregator
{
    // date_bin is native to PostgreSQL 14+ and maps one-to-one onto a time-series
    // extension's time_bucket, so replacing it later is a textual substitution in
    // this one file.
    private const string MinuteSql = """
        INSERT INTO metric_rollups_1m (series_id, bucket, count, sum, min, max, last)
        SELECT series_id,
               date_bin('1 minute', ts, timestamptz '2000-01-01'),
               count(*), sum(value), min(value), max(value),
               (array_agg(value ORDER BY ts DESC))[1]
        FROM metric_points
        WHERE ts >= @from AND ts < @to
        GROUP BY 1, 2
        ON CONFLICT (series_id, bucket) DO UPDATE
        SET count = excluded.count, sum = excluded.sum, min = excluded.min,
            max = excluded.max, last = excluded.last;
        """;

    // Reads the 1m table rather than raw points: an hour computed from sixty
    // already-computed minutes touches sixty rows per series instead of 360.
    private const string HourSql = """
        INSERT INTO metric_rollups_1h (series_id, bucket, count, sum, min, max, last)
        SELECT series_id,
               date_bin('1 hour', bucket, timestamptz '2000-01-01'),
               sum(count)::int, sum(sum), min(min), max(max),
               (array_agg(last ORDER BY bucket DESC))[1]
        FROM metric_rollups_1m
        WHERE bucket >= @from AND bucket < @to
        GROUP BY 1, 2
        ON CONFLICT (series_id, bucket) DO UPDATE
        SET count = excluded.count, sum = excluded.sum, min = excluded.min,
            max = excluded.max, last = excluded.last;
        """;

    public Task<int> AggregateMinutesAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        ExecuteAsync(MinuteSql, from, to, cancellationToken);

    public Task<int> AggregateHoursAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        ExecuteAsync(HourSql, from, to, cancellationToken);

    public async Task<DateTimeOffset?> OldestRawTimestampAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand("SELECT min(ts) FROM metric_points;", connection);

        var result = await command.ExecuteScalarAsync(cancellationToken);

        // timestamptz comes back as a DateTime with Kind=Utc, never a DateTimeOffset.
        return result is null or DBNull ? null : new DateTimeOffset((DateTime)result);
    }

    private async Task<int> ExecuteAsync(
        string sql, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("from", from.ToUniversalTime());
        command.Parameters.AddWithValue("to", to.ToUniversalTime());

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
