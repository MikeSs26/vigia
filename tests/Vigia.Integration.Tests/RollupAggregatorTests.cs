using Npgsql;
using Vigia.Cli;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;
using Vigia.Infrastructure.Rollups;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class RollupAggregatorTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Anchor =
        new(2031, 4, 7, 10, 0, 0, TimeSpan.Zero);

    private int _seriesId;

    private PostgresRollupAggregator Aggregator() => new(postgres.ConnectionString);

    public async Task InitializeAsync()
    {
        var maintenance = new PostgresPartitionMaintenance(postgres.ConnectionString);
        foreach (var table in new[] { "metric_points", "metric_rollups_1m", "metric_rollups_1h" })
        {
            await maintenance.EnsurePartitionsAsync(table, Anchor, 1, default);
        }

        int tenantId;
        int sourceId;
        await using (var context = postgres.CreateContext())
        {
            tenantId = await AdminCommands.CreateTenantAsync(
                context, "Rollup", $"rollup-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

            sourceId = await AdminCommands.CreateSourceAsync(
                context, tenantId, $"host-{Guid.NewGuid():N}", SourceKind.Host, default);
        }

        await using var connection = await postgres.OpenConnectionAsync();
        await using var series = new NpgsqlCommand(
            """
            INSERT INTO metric_series (tenant_id, source_id, name, unit, labels)
            VALUES (@t, @s, 'cpu.usage', 'percent', '{}') RETURNING id;
            """, connection);

        series.Parameters.AddWithValue("t", tenantId);
        series.Parameters.AddWithValue("s", sourceId);

        _seriesId = (int)(await series.ExecuteScalarAsync())!;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task InsertPointAsync(DateTimeOffset ts, double value)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO metric_points (series_id, ts, value) VALUES (@s, @ts, @v);", connection);

        command.Parameters.AddWithValue("s", _seriesId);
        command.Parameters.AddWithValue("ts", ts.ToUniversalTime());
        command.Parameters.AddWithValue("v", value);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<(int Count, double Sum, double Min, double Max, double Last)> ReadMinuteAsync(
        DateTimeOffset bucket)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count, sum, min, max, last FROM metric_rollups_1m
            WHERE series_id = @s AND bucket = @b;
            """, connection);

        command.Parameters.AddWithValue("s", _seriesId);
        command.Parameters.AddWithValue("b", bucket.ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (reader.GetInt32(0), reader.GetDouble(1), reader.GetDouble(2),
                reader.GetDouble(3), reader.GetDouble(4));
    }

    [Fact]
    public async Task MinuteBucketsCarryCountSumMinMaxAndLast()
    {
        await InsertPointAsync(Anchor.AddSeconds(0), 10.0);
        await InsertPointAsync(Anchor.AddSeconds(10), 30.0);
        await InsertPointAsync(Anchor.AddSeconds(20), 20.0);

        var written = await Aggregator().AggregateMinutesAsync(Anchor, Anchor.AddMinutes(1), default);

        Assert.Equal(1, written);

        var bucket = await ReadMinuteAsync(Anchor);

        Assert.Equal(3, bucket.Count);
        Assert.Equal(60.0, bucket.Sum, 6);
        Assert.Equal(10.0, bucket.Min, 6);
        Assert.Equal(30.0, bucket.Max, 6);

        // "last" is the newest point in the bucket by timestamp, not the largest.
        Assert.Equal(20.0, bucket.Last, 6);
    }

    [Fact]
    public async Task RunningTheSameRangeTwiceChangesNothing()
    {
        await InsertPointAsync(Anchor.AddMinutes(5).AddSeconds(1), 42.0);

        var from = Anchor.AddMinutes(5);
        await Aggregator().AggregateMinutesAsync(from, from.AddMinutes(1), default);
        await Aggregator().AggregateMinutesAsync(from, from.AddMinutes(1), default);

        var bucket = await ReadMinuteAsync(from);

        // Not doubled: the upsert replaces the row rather than adding to it. This
        // is the property that makes a crash between the write and the watermark
        // update survivable.
        Assert.Equal(1, bucket.Count);
        Assert.Equal(42.0, bucket.Sum, 6);
    }

    [Fact]
    public async Task ALatePointIsAbsorbedWhenTheBucketIsRecomputed()
    {
        var from = Anchor.AddMinutes(10);

        await InsertPointAsync(from.AddSeconds(1), 10.0);
        await Aggregator().AggregateMinutesAsync(from, from.AddMinutes(1), default);

        await InsertPointAsync(from.AddSeconds(30), 20.0);
        await Aggregator().AggregateMinutesAsync(from, from.AddMinutes(1), default);

        var bucket = await ReadMinuteAsync(from);

        Assert.Equal(2, bucket.Count);
        Assert.Equal(30.0, bucket.Sum, 6);
    }

    [Fact]
    public async Task HourBucketsAreComputedFromMinuteBuckets()
    {
        var from = Anchor.AddMinutes(20);

        await InsertPointAsync(from.AddSeconds(1), 10.0);
        await InsertPointAsync(from.AddMinutes(1).AddSeconds(1), 20.0);
        await InsertPointAsync(from.AddMinutes(2).AddSeconds(1), 30.0);

        await Aggregator().AggregateMinutesAsync(from, from.AddMinutes(3), default);
        await Aggregator().AggregateHoursAsync(Anchor, Anchor.AddHours(1), default);

        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count, sum, min, max, last FROM metric_rollups_1h
            WHERE series_id = @s AND bucket = @b;
            """, connection);

        command.Parameters.AddWithValue("s", _seriesId);
        command.Parameters.AddWithValue("b", Anchor.ToUniversalTime());

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        // Counts and sums add; min and max are the extremes of the extremes; last
        // comes from the newest minute bucket. This is what "re-aggregatable"
        // means, and it is why the tables store sum and count rather than an average.
        Assert.True(reader.GetInt32(0) >= 3);
        Assert.Equal(10.0, reader.GetDouble(2), 6);
        Assert.Equal(30.0, reader.GetDouble(3), 6);
        Assert.Equal(30.0, reader.GetDouble(4), 6);
    }

    [Fact]
    public async Task PointsOutsideTheRangeAreNotAggregated()
    {
        var from = Anchor.AddMinutes(30);

        await InsertPointAsync(from.AddSeconds(1), 1.0);
        await InsertPointAsync(from.AddMinutes(2), 2.0);

        // Half-open [from, from+1min): the second point must not appear.
        var written = await Aggregator().AggregateMinutesAsync(from, from.AddMinutes(1), default);

        Assert.Equal(1, written);
    }

    [Fact]
    public async Task OldestRawTimestampFindsSomethingOnceAPointExists()
    {
        await InsertPointAsync(Anchor.AddMinutes(40), 1.0);

        var oldest = await Aggregator().OldestRawTimestampAsync(default);

        Assert.NotNull(oldest);
        Assert.True(oldest!.Value <= Anchor.AddMinutes(40));
    }
}
