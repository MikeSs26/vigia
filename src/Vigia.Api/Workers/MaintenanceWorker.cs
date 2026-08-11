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

        var created = await maintenance.EnsurePartitionsAsync(
            "metric_points", now, _options.WeeksAhead, cancellationToken);

        if (created.Count > 0)
        {
            logger.LogInformation("Created partitions {Partitions}", string.Join(", ", created));
        }

        var dropped = await maintenance.DropExpiredAsync(
            "metric_points", now.AddDays(-_options.RawRetentionDays), cancellationToken);

        if (dropped.Count > 0)
        {
            logger.LogInformation("Dropped expired partitions {Partitions}", string.Join(", ", dropped));
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
