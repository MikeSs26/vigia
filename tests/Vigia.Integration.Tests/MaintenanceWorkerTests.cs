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
}
