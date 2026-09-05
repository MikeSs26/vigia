using Microsoft.Extensions.Options;
using Vigia.Infrastructure.Partitions;

namespace Vigia.Api.Workers;

/// <summary>
/// Keeps partitions ahead of incoming data and drops those past the retention
/// horizon. Runs before anything else on startup: a missing partition makes every
/// insert fail, so this cannot wait for the first timer tick.
/// </summary>
public sealed class MaintenanceWorker(
    IPartitionMaintenance maintenance,
    IOptions<MaintenanceOptions> options,
    TimeProvider timeProvider,
    ILogger<MaintenanceWorker> logger) : BackgroundService
{
    private readonly MaintenanceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(_options.IntervalMinutes), timeProvider);

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Partition maintenance failed; will retry on the next tick");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        // Each table keeps its own horizon: raw data is expensive and short-lived,
        // aggregates are cheap and are the only reason history survives at all.
        //
        // Every table also reaches BACKWARDS, because neither writer only ever
        // writes "now". Ingestion accepts timestamps up to the retention horizon
        // old, and the rollup worker seeds at the oldest raw point and recomputes a
        // trailing window behind its watermark. A long-running instance happens to
        // hold those past weeks already — it created them when they were current —
        // which is why only a fresh database or a long gap exposes the difference.
        var rawWeeksBehind = (int)Math.Ceiling(_options.RawRetentionDays / 7.0);

        (string Table, int RetentionDays, int WeeksBehind)[] tables =
        [
            ("metric_points", _options.RawRetentionDays, rawWeeksBehind),
            ("metric_rollups_1m", _options.MinuteRollupRetentionDays, _options.RollupWeeksBehind),
            ("metric_rollups_1h", _options.HourRollupRetentionDays, _options.RollupWeeksBehind),
        ];

        foreach (var (table, retentionDays, weeksBehind) in tables)
        {
            // EnsurePartitionsAsync walks forward from the week containing its
            // start, so reaching backwards is a matter of starting that many weeks
            // earlier and asking for that many more weeks.
            var created = await maintenance.EnsurePartitionsAsync(
                table,
                now.AddDays(-7 * weeksBehind),
                weeksBehind + _options.WeeksAhead,
                cancellationToken);

            if (created.Count > 0)
            {
                logger.LogInformation(
                    "Created partitions on {Table}: {Partitions}", table, string.Join(", ", created));
            }

            var dropped = await maintenance.DropExpiredAsync(
                table, now.AddDays(-retentionDays), cancellationToken);

            if (dropped.Count > 0)
            {
                logger.LogInformation(
                    "Dropped expired partitions on {Table}: {Partitions}", table, string.Join(", ", dropped));
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(
        PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
