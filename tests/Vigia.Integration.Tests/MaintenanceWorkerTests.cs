using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Vigia.Api.Workers;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class MaintenanceWorkerTests(PostgresFixture postgres)
{
    private async Task<bool> PartitionExistsAsync(string name)
    {
        await using var connection = await postgres.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@n) IS NOT NULL;", connection);
        command.Parameters.AddWithValue("n", name);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task CreatesPartitionsImmediatelyOnStartupWithoutWaitingForATick()
    {
        // A missing partition makes every insert fail, so maintenance cannot wait
        // for the first timer interval to elapse.
        var time = new FakeTimeProvider(new DateTimeOffset(2038, 9, 6, 0, 0, 0, TimeSpan.Zero));

        var worker = new MaintenanceWorker(
            new PostgresPartitionMaintenance(postgres.ConnectionString),
            Options.Create(new MaintenanceOptions
            {
                WeeksAhead = 2,
                RawRetentionDays = 7,
                IntervalMinutes = 60,
            }),
            time,
            NullLogger<MaintenanceWorker>.Instance);

        await worker.StartAsync(default);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await PartitionExistsAsync("metric_points_20380906") && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.True(await PartitionExistsAsync("metric_points_20380906"));
        Assert.True(await PartitionExistsAsync("metric_points_20380913"));
    }

    [Fact]
    public async Task CreatesRollupPartitionsBehindNowSoColdStartHistoryHasSomewhereToLand()
    {
        // The rollup worker seeds its watermark at the oldest raw point, which on a
        // populated database is up to two weeks old. Partitions only ever reaching
        // forward from this week left those buckets with nowhere to land: the insert
        // failed, the cycle retried forever, and the raw data expired unaggregated.
        // Raw points need no such reach — ingestion refuses stale timestamps.
        var time = new FakeTimeProvider(new DateTimeOffset(2039, 9, 7, 0, 0, 0, TimeSpan.Zero));

        var worker = new MaintenanceWorker(
            new PostgresPartitionMaintenance(postgres.ConnectionString),
            Options.Create(new MaintenanceOptions
            {
                WeeksAhead = 2,
                RollupWeeksBehind = 3,
                RawRetentionDays = 7,
                IntervalMinutes = 60,
            }),
            time,
            NullLogger<MaintenanceWorker>.Instance);

        await worker.StartAsync(default);

        // Wait on the last partition of the last table the cycle touches. Waiting
        // on a 1m partition instead would return while the 1h pass was still to
        // come, and StopAsync would then cancel it mid-cycle.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await PartitionExistsAsync("metric_rollups_1h_20390912") && !cts.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        await worker.StopAsync(CancellationToken.None);

        // Three weeks back from Wednesday 2039-09-07 is the week beginning Monday
        // 2039-08-15, and the reach must not skip the weeks in between.
        Assert.True(await PartitionExistsAsync("metric_rollups_1m_20390815"));
        Assert.True(await PartitionExistsAsync("metric_rollups_1m_20390822"));
        Assert.True(await PartitionExistsAsync("metric_rollups_1m_20390829"));
        Assert.True(await PartitionExistsAsync("metric_rollups_1h_20390815"));

        // Still reaching forward as well, not merely shifted backwards.
        Assert.True(await PartitionExistsAsync("metric_rollups_1m_20390905"));
        Assert.True(await PartitionExistsAsync("metric_rollups_1m_20390912"));

        // Raw points reach back too, one week for a 7-day horizon: ingestion accepts
        // timestamps that old, and on a fresh database the week they belong to has
        // never been created. The cycle that creates it also runs expiry, so its
        // surviving here is what shows the two horizons agree instead of fighting.
        Assert.True(await PartitionExistsAsync("metric_points_20390829"));
    }
}
