using Npgsql;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class AlertingSchemaTests(PostgresFixture postgres)
{
    private async Task<bool> ExistsAsync(string sql)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Theory]
    [InlineData("alert_rules")]
    [InlineData("alert_instances")]
    [InlineData("alert_events")]
    [InlineData("notification_channels")]
    [InlineData("notification_outbox")]
    [InlineData("silences")]
    public async Task TheAlertingTablesExist(string table)
    {
        Assert.True(await ExistsAsync($"SELECT to_regclass('{table}') IS NOT NULL;"));
    }

    [Fact]
    public async Task AnInstanceIsUniquePerRuleAndSource()
    {
        // A rule targeting every source produces one instance per source. The
        // constraint is what makes the evaluator's upsert idempotent on retry.
        Assert.True(await ExistsAsync(
            """
            SELECT EXISTS (
              SELECT 1 FROM pg_indexes
              WHERE tablename = 'alert_instances' AND indexdef LIKE '%UNIQUE%rule_id%source_id%');
            """));
    }

    [Fact]
    public async Task ASilenceCannotBeCreatedWithoutAnExpiry()
    {
        // Mandatory expiry is the whole reason the kill switch is a row rather
        // than a boolean: nothing ends up permanently muted and forgotten.
        Assert.True(await ExistsAsync(
            """
            SELECT is_nullable = 'NO' FROM information_schema.columns
            WHERE table_name = 'silences' AND column_name = 'until';
            """));
    }

    [Fact]
    public async Task TheOutboxIsIndexedForItsDrainQuery()
    {
        // The notifier polls "undelivered and due" every 10s. Without an index
        // that is a sequential scan of every message ever sent.
        Assert.True(await ExistsAsync(
            """
            SELECT EXISTS (
              SELECT 1 FROM pg_indexes
              WHERE tablename = 'notification_outbox' AND indexdef LIKE '%next_attempt_at%');
            """));
    }
}
