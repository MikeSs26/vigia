using Npgsql;
using Vigia.Cli;
using Vigia.Core.Querying;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;
using Vigia.Infrastructure.Querying;
using Vigia.Infrastructure.Rollups;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class MetricQueryReaderTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Anchor =
        new(2031, 11, 4, 6, 0, 0, TimeSpan.Zero);

    private int _tenantId;
    private int _sourceId;

    public async Task InitializeAsync()
    {
        var maintenance = new PostgresPartitionMaintenance(postgres.ConnectionString);
        foreach (var table in new[] { "metric_points", "metric_rollups_1m", "metric_rollups_1h" })
        {
            await maintenance.EnsurePartitionsAsync(table, Anchor, 1, default);
        }

        await using (var context = postgres.CreateContext())
        {
            _tenantId = await AdminCommands.CreateTenantAsync(
                context, "Reader", $"reader-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

            _sourceId = await AdminCommands.CreateSourceAsync(
                context, _tenantId, $"host-{Guid.NewGuid():N}", SourceKind.Host, default);
        }

        var plain = await CreateSeriesAsync("cpu.usage", "percent", "{}");
        var labelled = await CreateSeriesAsync("cpu.usage", "percent", """{"core": "1"}""");

        for (var i = 0; i < 3; i++)
        {
            await InsertPointAsync(plain, Anchor.AddSeconds(10 * i), 10.0 + i);
            await InsertPointAsync(labelled, Anchor.AddSeconds(10 * i), 50.0 + i);
        }

        await new PostgresRollupAggregator(postgres.ConnectionString)
            .AggregateMinutesAsync(Anchor, Anchor.AddMinutes(5), default);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<int> CreateSeriesAsync(string name, string unit, string labels)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
            VALUES (@t, @s, @n, @u, @l::jsonb) RETURNING id;
            """, connection);

        command.Parameters.AddWithValue("t", _tenantId);
        command.Parameters.AddWithValue("s", _sourceId);
        command.Parameters.AddWithValue("n", name);
        command.Parameters.AddWithValue("u", unit);
        command.Parameters.AddWithValue("l", labels);

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task InsertPointAsync(int seriesId, DateTimeOffset ts, double value)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO metric_points (series_id, ts, value) VALUES (@s, @ts, @v);", connection);

        command.Parameters.AddWithValue("s", seriesId);
        command.Parameters.AddWithValue("ts", ts.ToUniversalTime());
        command.Parameters.AddWithValue("v", value);

        await command.ExecuteNonQueryAsync();
    }

    private MetricQuery Query(Granularity granularity, Aggregation aggregation = Aggregation.Avg) =>
        new(_tenantId, _sourceId, "cpu.usage", Anchor, Anchor.AddMinutes(5), granularity, aggregation);

    private PostgresMetricQueryReader Reader() => new(postgres.ConnectionString);

    [Fact]
    public async Task RawReadsReturnEveryPoint()
    {
        var results = await Reader().ReadAsync(Query(Granularity.Raw), default);

        // Two series: the same metric name under two different label sets.
        Assert.Equal(2, results.Count);
        Assert.All(results, series => Assert.Equal(3, series.Points.Count));
    }

    [Fact]
    public async Task LabelSetsAreReturnedSeparatelyRatherThanAveragedTogether()
    {
        // cpu.usage on two different cores are different measurements. Collapsing
        // them would answer a question nobody asked.
        var results = await Reader().ReadAsync(Query(Granularity.Raw), default);

        Assert.Contains(results, r => r.Labels.Count == 0);
        Assert.Contains(results, r => r.Labels.TryGetValue("core", out var core) && core == "1");
    }

    [Fact]
    public async Task PointsComeBackInChronologicalOrder()
    {
        var results = await Reader().ReadAsync(Query(Granularity.Raw), default);
        var points = results.First(r => r.Labels.Count == 0).Points;

        Assert.Equal(points.OrderBy(p => p.Ts), points);
    }

    [Fact]
    public async Task AverageIsDerivedFromSumAndCount()
    {
        // 10 + 11 + 12 over three points.
        var results = await Reader().ReadAsync(Query(Granularity.OneMinute, Aggregation.Avg), default);
        var series = results.First(r => r.Labels.Count == 0);

        Assert.Equal(11.0, series.Points[0].Value, 6);
    }

    [Theory]
    [InlineData(Aggregation.Min, 10.0)]
    [InlineData(Aggregation.Max, 12.0)]
    [InlineData(Aggregation.Last, 12.0)]
    [InlineData(Aggregation.Count, 3.0)]
    public async Task EachAggregationSelectsItsOwnColumn(Aggregation aggregation, double expected)
    {
        var results = await Reader().ReadAsync(Query(Granularity.OneMinute, aggregation), default);
        var series = results.First(r => r.Labels.Count == 0);

        Assert.Equal(expected, series.Points[0].Value, 6);
    }

    [Fact]
    public async Task AnUnknownMetricNameReturnsNoSeriesRatherThanFailing()
    {
        var query = Query(Granularity.Raw) with { Name = "does.not.exist" };

        Assert.Empty(await Reader().ReadAsync(query, default));
    }

    [Fact]
    public async Task AnotherTenantSeesNothing()
    {
        // Tenancy is structural: every query filters on tenant_id, so a caller
        // cannot reach another tenant's series even knowing its source id.
        var query = Query(Granularity.Raw) with { TenantId = _tenantId + 10_000 };

        Assert.Empty(await Reader().ReadAsync(query, default));
    }

    [Fact]
    public async Task TheRangeIsHalfOpen()
    {
        var query = Query(Granularity.Raw) with { To = Anchor.AddSeconds(10) };
        var results = await Reader().ReadAsync(query, default);

        Assert.All(results, series => Assert.Single(series.Points));
    }
}
