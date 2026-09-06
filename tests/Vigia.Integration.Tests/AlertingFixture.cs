using Npgsql;
using Vigia.Core.Alerting;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Integration.Tests;

public readonly record struct AlertingSeed(
    int TenantId, int SourceId, int SeriesId, int RuleId, int ChannelId);

public static class AlertingFixture
{
    public static async Task<AlertingSeed> SeedAsync(
        PostgresFixture postgres, DateTimeOffset anchor,
        double threshold, int forSeconds, bool withChannel = true)
    {
        var maintenance = new PostgresPartitionMaintenance(postgres.ConnectionString);
        await maintenance.EnsurePartitionsAsync("metric_points", anchor, 1, default);

        await using var context = postgres.CreateContext();

        var tenant = new Tenant
        {
            Name = "Alerting",
            Slug = $"al-{Guid.NewGuid():N}",
            CreatedAt = anchor,
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var source = new Source
        {
            TenantId = tenant.Id,
            Name = $"h-{Guid.NewGuid():N}",
            Kind = SourceKind.Host,
        };
        context.Sources.Add(source);
        await context.SaveChangesAsync();

        var channel = new NotificationChannelEntity
        {
            TenantId = tenant.Id,
            Kind = "discord_webhook",
            Name = "ops",
            MinSeverity = Severity.Info,
            Enabled = true,
        };
        context.NotificationChannels.Add(channel);
        await context.SaveChangesAsync();

        var rule = new AlertRuleEntity
        {
            TenantId = tenant.Id,
            SourceId = source.Id,
            MetricName = "cpu.usage",
            Aggregation = RuleAggregation.Avg,
            WindowSeconds = 300,
            Operator = ComparisonOperator.Gt,
            Threshold = threshold,
            ForSeconds = forSeconds,
            NoDataAfterSeconds = 120,
            Severity = Severity.Warning,
            ChannelId = withChannel ? channel.Id : null,
            CooldownSeconds = 1800,
            Enabled = true,
        };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();

        await using var connection = await postgres.OpenConnectionAsync();
        await using var series = new NpgsqlCommand(
            """
            INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
            VALUES (@t, @s, 'cpu.usage', 'percent', '{}') RETURNING id;
            """, connection);

        series.Parameters.AddWithValue("t", tenant.Id);
        series.Parameters.AddWithValue("s", source.Id);

        var seriesId = (int)(await series.ExecuteScalarAsync())!;

        return new AlertingSeed(tenant.Id, source.Id, seriesId, rule.Id, channel.Id);
    }

    /// <summary>Thirty samples at 10s spacing, ending at <paramref name="upTo"/>.</summary>
    public static async Task WritePointsAsync(
        PostgresFixture postgres, int seriesId, DateTimeOffset upTo, double value)
    {
        await using var connection = await postgres.OpenConnectionAsync();

        for (var i = 0; i < 30; i++)
        {
            await using var command = new NpgsqlCommand(
                "INSERT INTO metric_points (series_id, ts, value) VALUES (@s, @ts, @v);", connection);

            command.Parameters.AddWithValue("s", seriesId);
            command.Parameters.AddWithValue("ts", upTo.AddSeconds(-10 * i).ToUniversalTime());
            command.Parameters.AddWithValue("v", value);

            await command.ExecuteNonQueryAsync();
        }
    }
}
