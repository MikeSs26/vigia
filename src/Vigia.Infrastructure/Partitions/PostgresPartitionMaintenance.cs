using System.Globalization;
using Npgsql;

namespace Vigia.Infrastructure.Partitions;

public sealed class PostgresPartitionMaintenance(string connectionString) : IPartitionMaintenance
{
    private static readonly string[] AllowedTables = ["metric_points"];

    public async Task<IReadOnlyList<string>> EnsurePartitionsAsync(
        string table, DateTimeOffset from, int weeksAhead, CancellationToken cancellationToken)
    {
        GuardTable(table);

        var created = new List<string>();
        var start = StartOfWeek(from);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        for (var week = 0; week < weeksAhead; week++)
        {
            var lower = start.AddDays(7 * week);
            var upper = lower.AddDays(7);
            var name = PartitionName(table, lower);

            // Identifiers cannot be parameterised, which is why the table name is
            // whitelisted above and the suffix is derived from a date rather than
            // from any caller-supplied string.
            var sql = $"""
                CREATE TABLE IF NOT EXISTS {name}
                PARTITION OF {table}
                FOR VALUES FROM ('{Iso(lower)}') TO ('{Iso(upper)}');
                """;

            await using var command = new NpgsqlCommand(sql, connection);
            var existedBefore = await PartitionExistsAsync(connection, name, cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);

            if (!existedBefore)
            {
                created.Add(name);
            }
        }

        return created;
    }

    public async Task<IReadOnlyList<string>> DropExpiredAsync(
        string table, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        GuardTable(table);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // pg_get_expr yields the FOR VALUES clause; the upper bound decides
        // eligibility, so a partition still holding live rows is never dropped.
        await using var query = new NpgsqlCommand(
            """
            SELECT child.relname, pg_get_expr(child.relpartbound, child.oid)
            FROM pg_inherits i
            JOIN pg_class parent ON parent.oid = i.inhparent
            JOIN pg_class child  ON child.oid  = i.inhrelid
            WHERE parent.relname = @table;
            """, connection);
        query.Parameters.AddWithValue("table", table);

        var candidates = new List<string>();

        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString(0);
                var bound = reader.GetString(1);

                if (TryParseUpperBound(bound, out var upper) && upper <= olderThan)
                {
                    candidates.Add(name);
                }
            }
        }

        foreach (var name in candidates)
        {
            await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {name};", connection);
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        return candidates;
    }

    private static async Task<bool> PartitionExistsAsync(
        NpgsqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@name) IS NOT NULL;", connection);
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static bool TryParseUpperBound(string partitionBound, out DateTimeOffset upper)
    {
        upper = default;

        // Shape: FOR VALUES FROM ('2031-01-06 00:00:00+00') TO ('2031-01-13 00:00:00+00')
        var marker = partitionBound.LastIndexOf("TO (", StringComparison.Ordinal);
        if (marker < 0)
        {
            return false;
        }

        var open = partitionBound.IndexOf('\'', marker);
        var close = partitionBound.IndexOf('\'', open + 1);
        if (open < 0 || close < 0)
        {
            return false;
        }

        var literal = partitionBound[(open + 1)..close];
        return DateTimeOffset.TryParse(
            literal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out upper);
    }

    private static void GuardTable(string table)
    {
        if (!AllowedTables.Contains(table))
        {
            throw new ArgumentOutOfRangeException(nameof(table), table, "Not a partitioned table.");
        }
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        var date = new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero);
        var offset = ((int)date.DayOfWeek + 6) % 7; // Monday as the first day
        return date.AddDays(-offset);
    }

    private static string PartitionName(string table, DateTimeOffset weekStart) =>
        $"{table}_{weekStart:yyyyMMdd}";

    private static string Iso(DateTimeOffset instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture);
}
