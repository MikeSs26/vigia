using Microsoft.Extensions.Logging;
using Npgsql;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class PartitionMaintenanceTests(PostgresFixture postgres)
{
    private PostgresPartitionMaintenance Maintenance() => new(postgres.ConnectionString);

    /// <summary>Captures warning-level log messages so a test can assert a skip
    /// was reported rather than silent, without pulling in a mocking library.</summary>
    private sealed class CapturingLogger : ILogger<PostgresPartitionMaintenance>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private async Task<int> PartitionCountAsync()
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*) FROM pg_inherits i
            JOIN pg_class parent ON parent.oid = i.inhparent
            WHERE parent.relname = 'metric_points';
            """, connection);

        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task CreatesOnePartitionPerWeekAheadOfTime()
    {
        var before = await PartitionCountAsync();

        var created = await Maintenance().EnsurePartitionsAsync(
            "metric_points", new DateTimeOffset(2031, 1, 8, 0, 0, 0, TimeSpan.Zero), 3, default);

        Assert.Equal(3, created.Count);
        Assert.Equal(before + 3, await PartitionCountAsync());
    }

    [Fact]
    public async Task IsIdempotentWhenPartitionsAlreadyExist()
    {
        var from = new DateTimeOffset(2032, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var first = await Maintenance().EnsurePartitionsAsync("metric_points", from, 2, default);
        var second = await Maintenance().EnsurePartitionsAsync("metric_points", from, 2, default);

        Assert.Equal(2, first.Count);
        Assert.Empty(second);
    }

    [Fact]
    public async Task DropsOnlyPartitionsEntirelyOlderThanTheHorizon()
    {
        // 2033-06-06 is a Monday, so the three partitions start on the 6th, 13th
        // and 20th and end on the 13th, 20th and 27th.
        var from = new DateTimeOffset(2033, 6, 6, 0, 0, 0, TimeSpan.Zero);
        await Maintenance().EnsurePartitionsAsync("metric_points", from, 3, default);

        // The horizon falls inside the third week, so only the first two are
        // fully expired and eligible.
        var dropped = await Maintenance().DropExpiredAsync(
            "metric_points", from.AddDays(17), default);

        // Assert on names, not on the count: this container is shared with every
        // other test class, so partitions seeded elsewhere with earlier dates are
        // also legitimately dropped by this call and would break a count check.
        Assert.Contains("metric_points_20330606", dropped);
        Assert.Contains("metric_points_20330613", dropped);
        Assert.DoesNotContain("metric_points_20330620", dropped);
    }

    [Fact]
    public async Task SkipsAndLogsAPartitionWhoseNameDoesNotMatchTheSchemeButStillDropsNormalOnes()
    {
        var logger = new CapturingLogger();
        var maintenance = new PostgresPartitionMaintenance(postgres.ConnectionString, logger);

        // A normal partition this component named and therefore can retire.
        var from = new DateTimeOffset(2040, 2, 5, 0, 0, 0, TimeSpan.Zero);
        var created = await maintenance.EnsurePartitionsAsync("metric_points", from, 1, default);
        var normalPartition = Assert.Single(created);

        // A rogue partition, on a week nothing else in the suite uses, whose name
        // does not fit "{table}_yyyyMMdd" — stands in for a hand-crafted or
        // legacy partition this component never named.
        const string roguePartition = "metric_points_rogue_partition";
        await using (var connection = await postgres.OpenConnectionAsync())
        {
            await using var create = new NpgsqlCommand(
                $"""
                CREATE TABLE {roguePartition} PARTITION OF metric_points
                FOR VALUES FROM ('2041-01-01 00:00:00+00') TO ('2041-01-08 00:00:00+00');
                """, connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var dropped = await maintenance.DropExpiredAsync(
                "metric_points", new DateTimeOffset(2042, 1, 1, 0, 0, 0, TimeSpan.Zero), default);

            // Normal drop still works...
            Assert.Contains(normalPartition, dropped);

            // ...but the unparseable one is left alone rather than guessed at...
            Assert.DoesNotContain(roguePartition, dropped);

            // ...and the skip is reported, not silent.
            Assert.Contains(logger.Warnings, warning =>
                warning.Contains(roguePartition, StringComparison.Ordinal));
        }
        finally
        {
            // DropExpiredAsync will never remove this partition itself (that is
            // the point of the test), so it must be cleaned up manually or it
            // would linger in the shared database forever.
            await using var connection = await postgres.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand($"DROP TABLE IF EXISTS {roguePartition};", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
