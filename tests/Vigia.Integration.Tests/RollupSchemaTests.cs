using Npgsql;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class RollupSchemaTests(PostgresFixture postgres)
{
    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@t) IS NOT NULL;", connection);
        command.Parameters.AddWithValue("t", table);

        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData("metric_rollups_1m")]
    [InlineData("metric_rollups_1h")]
    [InlineData("rollup_watermarks")]
    public async Task MigrationCreatesTheRollupTables(string table)
    {
        Assert.True(await TableExistsAsync(table));
    }

    [Theory]
    [InlineData("metric_rollups_1m")]
    [InlineData("metric_rollups_1h")]
    public async Task RollupTablesArePartitionedByRange(string table)
    {
        // 'r' is RANGE. A rollup table that is not partitioned would still accept
        // writes, and the failure would only surface months later when retention
        // tried to drop a partition that does not exist.
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT p.partstrat
            FROM pg_partitioned_table p
            JOIN pg_class c ON c.oid = p.partrelid
            WHERE c.relname = @t;
            """, connection);
        command.Parameters.AddWithValue("t", table);

        Assert.Equal('r', (char)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task WatermarkTableKeepsOneRowPerGranularity()
    {
        await using var connection = await postgres.OpenConnectionAsync();

        await using (var insert = new NpgsqlCommand(
            """
            INSERT INTO rollup_watermarks (granularity, watermark, updated_at)
            VALUES ('probe', now(), now())
            ON CONFLICT (granularity) DO UPDATE SET watermark = excluded.watermark;
            """, connection))
        {
            await insert.ExecuteNonQueryAsync();
            await insert.ExecuteNonQueryAsync();
        }

        await using (var count = new NpgsqlCommand(
            "SELECT count(*) FROM rollup_watermarks WHERE granularity = 'probe';", connection))
        {
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
        }

        await using var cleanup = new NpgsqlCommand(
            "DELETE FROM rollup_watermarks WHERE granularity = 'probe';", connection);
        await cleanup.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task MaintenanceCreatesPartitionsForTheRollupTables()
    {
        var anchor = new DateTimeOffset(2031, 3, 3, 0, 0, 0, TimeSpan.Zero);
        var maintenance = new PostgresPartitionMaintenance(postgres.ConnectionString);

        var created = await maintenance.EnsurePartitionsAsync("metric_rollups_1m", anchor, 2, default);

        Assert.Equal(2, created.Count);

        // Idempotent: a second call over the same weeks creates nothing.
        var again = await maintenance.EnsurePartitionsAsync("metric_rollups_1m", anchor, 2, default);
        Assert.Empty(again);

        var dropped = await maintenance.DropExpiredAsync(
            "metric_rollups_1m", anchor.AddDays(30), default);
        Assert.Equal(2, dropped.Count);
    }
}
