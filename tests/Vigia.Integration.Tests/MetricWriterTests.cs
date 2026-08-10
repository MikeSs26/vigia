using Npgsql;
using Vigia.Core;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;
using Vigia.Infrastructure.Series;
using Vigia.Infrastructure.Writing;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class MetricWriterTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Anchor =
        new(2034, 2, 6, 12, 0, 0, TimeSpan.Zero);

    private async Task<int> SeedSeriesAsync(string metric)
    {
        await using var context = postgres.CreateContext();

        var tenant = new Tenant
        {
            Name = "Writer",
            Slug = $"writer-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UnixEpoch,
        };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var source = new Source { TenantId = tenant.Id, Name = "host", Kind = SourceKind.Host };
        context.Sources.Add(source);
        await context.SaveChangesAsync();

        var resolver = new SeriesResolver(postgres.ConnectionString);
        return await resolver.ResolveAsync(
            new SeriesKey(tenant.Id, source.Id, metric, "percent", "{}"), default);
    }

    [Fact]
    public async Task WritesEveryPointInTheBatch()
    {
        var seriesId = await SeedSeriesAsync("cpu.usage");
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        var points = Enumerable.Range(0, 500)
            .Select(i => new ResolvedPoint(seriesId, Anchor.AddSeconds(i), i * 0.5))
            .ToList();

        var written = await new NpgsqlCopyMetricWriter(postgres.ConnectionString)
            .WriteAsync(points, default);

        Assert.Equal(500, written);

        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM metric_points WHERE series_id = @s;", connection);
        command.Parameters.AddWithValue("s", seriesId);

        Assert.Equal(500, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RowsLandInThePartitionCoveringTheirTimestamp()
    {
        var seriesId = await SeedSeriesAsync("disk.free_bytes");
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        await new NpgsqlCopyMetricWriter(postgres.ConnectionString)
            .WriteAsync([new ResolvedPoint(seriesId, Anchor, 1.0)], default);

        // The week of 2034-02-06 starts on Monday 2034-02-06.
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM metric_points_20340206 WHERE series_id = @s;", connection);
        command.Parameters.AddWithValue("s", seriesId);

        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task WritingAnEmptyBatchIsANoOp()
    {
        var written = await new NpgsqlCopyMetricWriter(postgres.ConnectionString)
            .WriteAsync([], default);

        Assert.Equal(0, written);
    }

    [Fact]
    public async Task FailsLoudlyWhenNoPartitionCoversTheTimestamp()
    {
        var seriesId = await SeedSeriesAsync("mem.used");
        var uncovered = new DateTimeOffset(2045, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<PostgresException>(() =>
            new NpgsqlCopyMetricWriter(postgres.ConnectionString)
                .WriteAsync([new ResolvedPoint(seriesId, uncovered, 1.0)], default));
    }
}
