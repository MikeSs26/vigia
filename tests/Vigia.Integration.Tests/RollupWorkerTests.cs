using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Vigia.Api.Workers;
using Vigia.Cli;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Partitions;
using Vigia.Infrastructure.Rollups;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class RollupWorkerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Anchor =
        new(2031, 9, 1, 8, 0, 0, TimeSpan.Zero);

    private readonly string _minuteKey = $"1m-{Guid.NewGuid():N}";
    private readonly string _hourKey = $"1h-{Guid.NewGuid():N}";

    private int _seriesId;

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
                context, "Worker", $"worker-{Guid.NewGuid():N}", DateTimeOffset.UnixEpoch, default);

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

    private RollupWorker Worker(FakeTimeProvider time, int maxMinuteBuckets = 1440) =>
        new(new PostgresRollupAggregator(postgres.ConnectionString),
            new PostgresRollupWatermarkStore(postgres.ConnectionString),
            Options.Create(new RollupOptions
            {
                MinuteGranularityKey = _minuteKey,
                HourGranularityKey = _hourKey,
                MaxMinuteBucketsPerCycle = maxMinuteBuckets,
                MaxHourBucketsPerCycle = 720,
            }),
            time,
            NullLogger<RollupWorker>.Instance);

    private async Task<DateTimeOffset?> WatermarkAsync(string granularityKey) =>
        await new PostgresRollupWatermarkStore(postgres.ConnectionString)
            .ReadAsync(granularityKey, default);

    /// <summary>
    /// Pins where this test's worker starts. Cold-start seeding reads the oldest
    /// raw point in the whole table, and the suite's other fixtures write points
    /// months earlier — without pinning, a bounded cycle spends itself on their
    /// data and never reaches this test's window.
    /// </summary>
    private async Task SeedWatermarksAsync(DateTimeOffset at)
    {
        var store = new PostgresRollupWatermarkStore(postgres.ConnectionString);

        await store.WriteAsync(_minuteKey, at, at, default);
        await store.WriteAsync(_hourKey, at, at, default);
    }

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

    private async Task<int> MinuteBucketCountAsync()
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM metric_rollups_1m WHERE series_id = @s;", connection);
        command.Parameters.AddWithValue("s", _seriesId);

        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task AColdStartAggregatesTheHistoryThatAlreadyExists()
    {
        // The reason the watermark exists: points written before the worker ever
        // ran must not be skipped, or a system deployed after its agent loses
        // everything measured in between.
        for (var i = 0; i < 5; i++)
        {
            await InsertPointAsync(Anchor.AddMinutes(i), 10.0 + i);
        }

        // No watermark is written first: this is the cold start. The cap is wide
        // enough to cover whatever history the shared database already holds in
        // one cycle, so the test measures the seeding behaviour rather than how
        // many cycles the catch-up happens to need.
        await Worker(new FakeTimeProvider(Anchor.AddMinutes(10)), maxMinuteBuckets: 10_000_000)
            .RunCycleAsync(default);

        Assert.Equal(5, await MinuteBucketCountAsync());
    }

    [Fact]
    public async Task TheWatermarkAdvancesToTheBucketInProgress()
    {
        await SeedWatermarksAsync(Anchor);
        await InsertPointAsync(Anchor.AddMinutes(1), 1.0);

        var now = Anchor.AddMinutes(10).AddSeconds(30);
        await Worker(new FakeTimeProvider(now)).RunCycleAsync(default);

        var watermark = await new PostgresRollupWatermarkStore(postgres.ConnectionString)
            .ReadAsync(_minuteKey, default);

        // Floored to the minute: the bucket currently in progress is never written,
        // because it is not finished and would be wrong until it is.
        Assert.Equal(Anchor.AddMinutes(10), watermark);
    }

    [Fact]
    public async Task ASecondCycleWithNoNewDataIsHarmless()
    {
        await SeedWatermarksAsync(Anchor);
        await InsertPointAsync(Anchor.AddMinutes(2), 5.0);

        var time = new FakeTimeProvider(Anchor.AddMinutes(10));
        await Worker(time).RunCycleAsync(default);
        var after = await MinuteBucketCountAsync();

        await Worker(time).RunCycleAsync(default);

        Assert.Equal(after, await MinuteBucketCountAsync());
    }

    [Fact]
    public async Task ABacklogLongerThanTheCapConvergesOverSeveralCycles()
    {
        // A single statement over an unbounded backlog is what gets killed on a
        // 1 GB host. The cap turns that into several small statements.
        await SeedWatermarksAsync(Anchor);

        for (var i = 0; i < 12; i++)
        {
            await InsertPointAsync(Anchor.AddMinutes(i), i);
        }

        var worker = Worker(new FakeTimeProvider(Anchor.AddMinutes(30)), maxMinuteBuckets: 5);

        await worker.RunCycleAsync(default);
        Assert.True(await MinuteBucketCountAsync() < 12);

        for (var cycle = 0; cycle < 6; cycle++)
        {
            await worker.RunCycleAsync(default);
        }

        Assert.Equal(12, await MinuteBucketCountAsync());
    }

    [Fact]
    public async Task ALatePointInAnAlreadyComputedBucketIsAbsorbedByTheTrailingRecompute()
    {
        var bucket = Anchor.AddMinutes(3);

        await SeedWatermarksAsync(Anchor);
        await InsertPointAsync(bucket.AddSeconds(1), 10.0);

        var time = new FakeTimeProvider(bucket.AddMinutes(1).AddSeconds(30));
        var worker = Worker(time);
        await worker.RunCycleAsync(default);

        await InsertPointAsync(bucket.AddSeconds(40), 20.0);

        time.SetUtcNow(bucket.AddMinutes(2).AddSeconds(30));
        await worker.RunCycleAsync(default);

        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count FROM metric_rollups_1m WHERE series_id = @s AND bucket = @b;", connection);
        command.Parameters.AddWithValue("s", _seriesId);
        command.Parameters.AddWithValue("b", bucket.ToUniversalTime());

        Assert.Equal(2, (int)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task TheHourWatermarkNeverOutrunsTheMinutesItIsComputedFrom()
    {
        // Hours are aggregated from the minute table, and the two passes are capped
        // at wildly different rates (1,440 minutes against 720 hours per cycle).
        // Left unclamped, the hour pass reads minutes that have not been written
        // yet, stores whatever fraction it finds, and marks those hours complete
        // forever — wrong data, presented as correct, in the year-long archive.
        await SeedWatermarksAsync(Anchor);

        for (var i = 0; i < 5; i++)
        {
            await InsertPointAsync(Anchor.AddMinutes(i), 10.0 + i);
        }

        // Minutes may cover one hour per cycle; hours would otherwise jump five.
        await Worker(new FakeTimeProvider(Anchor.AddHours(5)), maxMinuteBuckets: 60)
            .RunCycleAsync(default);

        var minutes = await WatermarkAsync(_minuteKey);
        var hours = await WatermarkAsync(_hourKey);

        Assert.NotNull(minutes);
        Assert.NotNull(hours);
        Assert.True(
            hours <= minutes,
            $"hour watermark {hours:o} claims hours the minute watermark {minutes:o} has not reached");
    }

    [Fact]
    public async Task APointArrivingLongAfterItsBucketIsStillAggregated()
    {
        // The agent spools through an outage and replays one batch per tick, so a
        // point can commit an hour or more behind the watermark. A one-bucket
        // trailing window let every such point fall straight through the rollups
        // and vanish when its raw partition expired — silently negating the spool.
        var bucket = Anchor.AddMinutes(1);

        await SeedWatermarksAsync(Anchor);
        await InsertPointAsync(bucket.AddSeconds(1), 10.0);

        var time = new FakeTimeProvider(Anchor.AddMinutes(90));
        var worker = Worker(time);
        await worker.RunCycleAsync(default);

        // Replayed from the spool an hour and a half after it was measured.
        await InsertPointAsync(bucket.AddSeconds(40), 20.0);

        time.SetUtcNow(Anchor.AddMinutes(91));
        await worker.RunCycleAsync(default);

        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count FROM metric_rollups_1m WHERE series_id = @s AND bucket = @b;", connection);
        command.Parameters.AddWithValue("s", _seriesId);
        command.Parameters.AddWithValue("b", bucket.ToUniversalTime());

        Assert.Equal(2, (int)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task HourBucketsAppearInTheSameCycleAsTheMinutesTheyCover()
    {
        await SeedWatermarksAsync(Anchor);
        await InsertPointAsync(Anchor.AddMinutes(1), 10.0);

        await Worker(new FakeTimeProvider(Anchor.AddHours(2))).RunCycleAsync(default);

        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM metric_rollups_1h WHERE series_id = @s;", connection);
        command.Parameters.AddWithValue("s", _seriesId);

        Assert.True((long)(await command.ExecuteScalarAsync())! >= 1);
    }
}
