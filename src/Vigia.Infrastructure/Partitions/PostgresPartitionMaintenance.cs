using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Vigia.Infrastructure.Partitions;

public sealed class PostgresPartitionMaintenance(
    string connectionString, ILogger<PostgresPartitionMaintenance>? logger = null) : IPartitionMaintenance
{
    private static readonly string[] AllowedTables = ["metric_points"];

    // Arbitrary namespace for the first key of pg_advisory_xact_lock(int, int).
    // Scopes our locks away from any other subsystem that might also take
    // advisory locks against this database, now or later.
    private const int AdvisoryLockNamespace = 8_985_031;

    private readonly ILogger<PostgresPartitionMaintenance> _logger =
        logger ?? NullLogger<PostgresPartitionMaintenance>.Instance;

    public async Task<IReadOnlyList<string>> EnsurePartitionsAsync(
        string table, DateTimeOffset from, int weeksAhead, CancellationToken cancellationToken)
    {
        GuardTable(table);

        var created = new List<string>();
        var start = StartOfWeek(from);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Serialises concurrent EnsurePartitionsAsync calls against the same table
        // (overlapping timer ticks, multiple instances) so the "created" list this
        // method returns stays accurate instead of two racing callers both seeing
        // existedBefore == false for the same week. hashtext is deterministic
        // across sessions, so every caller derives the same lock key for the same
        // table. The lock is released automatically when the transaction ends.
        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@ns, hashtext(@table));", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("ns", AdvisoryLockNamespace);
            lockCommand.Parameters.AddWithValue("table", table);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

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

            var existedBefore = await PartitionExistsAsync(connection, transaction, name, cancellationToken);

            try
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateTable)
            {
                // Complement to the advisory lock, not a substitute: under
                // sufficiently tight concurrency CREATE TABLE IF NOT EXISTS is not
                // fully atomic in PostgreSQL and can still raise a duplicate-object
                // error instead of silently no-op-ing. Treat that the same as
                // "already existed" rather than letting it fail the whole call.
                existedBefore = true;
            }

            if (!existedBefore)
            {
                created.Add(name);
            }
        }

        await transaction.CommitAsync(cancellationToken);

        return created;
    }

    public async Task<IReadOnlyList<string>> DropExpiredAsync(
        string table, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        GuardTable(table);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Only the partition NAME is read here, not its rendered FOR VALUES
        // clause: this component owns the naming scheme (EnsurePartitionsAsync
        // creates "{table}_yyyyMMdd" where the suffix is the Monday of the week
        // and the upper bound is that date plus seven days), so the upper bound
        // can be derived from the name with no dependency on how PostgreSQL
        // renders timestamps under the session's DateStyle. Parsing pg_get_expr's
        // rendered text was tried first and found to break silently whenever
        // DateStyle wasn't ISO/MDY (Postgres/SQL/German styles all render the
        // bound differently and none of them round-tripped through
        // DateTimeOffset.TryParse), which would have left expired partitions
        // permanently undroppable with no signal to an operator.
        await using var query = new NpgsqlCommand(
            """
            SELECT child.relname
            FROM pg_inherits i
            JOIN pg_class parent ON parent.oid = i.inhparent
            JOIN pg_class child  ON child.oid  = i.inhrelid
            WHERE parent.relname = @table;
            """, connection);
        query.Parameters.AddWithValue("table", table);

        var names = new List<string>();

        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }

        var candidates = new List<string>();

        foreach (var name in names)
        {
            if (!TryParsePartitionWeek(table, name, out var weekStart))
            {
                // Not one of ours (or its name has been tampered with): skip it
                // rather than guess, and say so loudly instead of silently
                // dropping it from consideration.
                _logger.LogWarning(
                    "Partition {PartitionName} on table {Table} does not match the "
                    + "expected \"{Table}_yyyyMMdd\" naming scheme; skipping it during "
                    + "expiry so retention does not act on a partition it cannot "
                    + "confidently interpret.",
                    name, table, table);
                continue;
            }

            var upper = weekStart.AddDays(7);
            if (upper <= olderThan)
            {
                candidates.Add(name);
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
        NpgsqlConnection connection, NpgsqlTransaction transaction, string name, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@name) IS NOT NULL;", connection, transaction);
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// Recovers the Monday that starts a partition's week directly from the name
    /// this component minted for it ("{table}_yyyyMMdd"), rather than from
    /// PostgreSQL's rendered partition bound. Returns false for any name that
    /// does not fit that shape.
    /// </summary>
    private static bool TryParsePartitionWeek(string table, string partitionName, out DateTimeOffset weekStart)
    {
        weekStart = default;

        var prefix = $"{table}_";
        if (!partitionName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = partitionName[prefix.Length..];
        if (suffix.Length != 8 || !suffix.All(char.IsAsciiDigit))
        {
            return false;
        }

        return DateTimeOffset.TryParseExact(
            suffix, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out weekStart);
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
