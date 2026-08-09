using Npgsql;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class PartitionMaintenanceTests(PostgresFixture postgres)
{
    private PostgresPartitionMaintenance Maintenance() => new(postgres.ConnectionString);

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
}
