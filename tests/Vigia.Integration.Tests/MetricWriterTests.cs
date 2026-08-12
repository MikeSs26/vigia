using Microsoft.Extensions.Logging.Abstractions;
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

    private NpgsqlCopyMetricWriter Writer() =>
        new(postgres.ConnectionString, NullLogger<NpgsqlCopyMetricWriter>.Instance);

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

        var written = await Writer().WriteAsync(points, default);

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

        await Writer().WriteAsync([new ResolvedPoint(seriesId, Anchor, 1.0)], default);

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
        var written = await Writer().WriteAsync([], default);

        Assert.Equal(0, written);
    }

    [Fact]
    public async Task FailsLoudlyWhenNoPartitionCoversTheTimestamp()
    {
        var seriesId = await SeedSeriesAsync("mem.used");
        var uncovered = new DateTimeOffset(2045, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await Assert.ThrowsAsync<PostgresException>(() =>
            Writer().WriteAsync([new ResolvedPoint(seriesId, uncovered, 1.0)], default));
    }

    [Fact]
    public async Task NonUtcOffsetIsNormalisedInsteadOfAbortingTheWholeBatch()
    {
        // This is the C1 finding: Npgsql refuses to write a DateTimeOffset with
        // a non-zero offset to timestamptz ("only offset 0 (UTC) is
        // supported"), and pre-fix, one such point in the middle of a COPY
        // aborted the entire binary import — losing every other point in the
        // batch alongside it, not just the bad one. The endpoint now
        // normalises before this point is ever reached, but this test drives
        // the writer directly, bypassing that normalisation, to prove the
        // writer's own defence-in-depth actually holds: the good points must
        // not be lost just because one point in the same batch carries a
        // residual non-UTC offset.
        var seriesId = await SeedSeriesAsync("mem.used_bytes");
        await new PostgresPartitionMaintenance(postgres.ConnectionString)
            .EnsurePartitionsAsync("metric_points", Anchor, 1, default);

        // Same instant as Anchor.AddSeconds(1), carried over the wire with a
        // +02:00 offset instead of UTC — exactly the shape of value
        // System.Text.Json would hand back from a client-supplied
        // "2034-02-06T14:00:01+02:00".
        var offsetInstant = Anchor.AddSeconds(1).ToOffset(TimeSpan.FromHours(2));

        var points = new List<ResolvedPoint>
        {
            new(seriesId, Anchor, 1.0),
            new(seriesId, offsetInstant, 2.0),
            new(seriesId, Anchor.AddSeconds(2), 3.0),
        };

        var written = await Writer().WriteAsync(points, default);

        Assert.Equal(3, written);

        await using var connection = await postgres.OpenConnectionAsync();
        await using var countCommand = new NpgsqlCommand(
            "SELECT count(*) FROM metric_points WHERE series_id = @s;", connection);
        countCommand.Parameters.AddWithValue("s", seriesId);
        Assert.Equal(3, Convert.ToInt32(await countCommand.ExecuteScalarAsync()));

        await using var tsCommand = new NpgsqlCommand(
            "SELECT ts FROM metric_points WHERE series_id = @s AND value = 2.0;", connection);
        tsCommand.Parameters.AddWithValue("s", seriesId);
        await using var reader = await tsCommand.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var stored = reader.GetFieldValue<DateTimeOffset>(0);
        Assert.Equal(offsetInstant.UtcDateTime, stored.UtcDateTime);
        Assert.Equal(TimeSpan.Zero, stored.Offset);
    }
}
