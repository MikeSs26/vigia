using System.Text.Json;
using Microsoft.Extensions.Options;
using Vigia.Agent.Collection;
using Vigia.Agent.Publishing;
using Vigia.Agent.Spool;

namespace Vigia.Agent;

public sealed class AgentWorker(
    IMetricCollector collector,
    IBatchSpool spool,
    IBatchPublisher publisher,
    IOptions<AgentOptions> options,
    TimeProvider timeProvider,
    ILogger<AgentWorker> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);

    private readonly AgentOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.IntervalSeconds), timeProvider);

        logger.LogInformation(
            "Agent reporting source {Source} to {Endpoint} every {Interval}s; spool at {Spool}",
            _options.SourceName, _options.Endpoint, _options.IntervalSeconds, _options.SpoolDirectory);

        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad cycle must not end the agent; the next tick retries.
                logger.LogError(ex, "Cycle failed; continuing");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await DrainSpoolAsync(cancellationToken);

        var metrics = collector.Collect();
        if (metrics.Count == 0)
        {
            return;
        }

        var payload = Serialise(metrics);
        var outcome = await publisher.PublishAsync(payload, cancellationToken);

        if (outcome == PublishOutcome.Retry)
        {
            spool.Park(payload, timeProvider.GetUtcNow());
            logger.LogInformation("Parked a batch; spool now holds {Count}", spool.Count);
        }
    }

    /// <summary>
    /// Sends the backlog before anything new, oldest first. One batch per cycle
    /// is deliberate: a burst of hundreds after an outage would collide with the
    /// API's own rate limit and turn recovery into a second outage.
    /// </summary>
    private async Task DrainSpoolAsync(CancellationToken cancellationToken)
    {
        if (!spool.TryTakeOldest(out var batch))
        {
            return;
        }

        var outcome = await publisher.PublishAsync(batch.Payload, cancellationToken);

        switch (outcome)
        {
            case PublishOutcome.Accepted:
                spool.Discard(batch);
                logger.LogInformation("Delivered a spooled batch; {Count} remain", spool.Count);
                break;

            case PublishOutcome.Rejected:
                spool.Discard(batch);
                logger.LogError(
                    "Dropped a spooled batch parked at {ParkedAt} that the API refuses permanently",
                    batch.ParkedAt);
                break;

            case PublishOutcome.Retry:
                // Leave it where it is; the next cycle tries again.
                break;
        }
    }

    private string Serialise(IReadOnlyList<HostMetric> metrics)
    {
        var now = timeProvider.GetUtcNow();

        var request = new
        {
            source = _options.SourceName,
            points = metrics.Select(m => new
            {
                name = m.Name,
                unit = m.Unit,
                ts = now,
                value = m.Value,
            }),
        };

        return JsonSerializer.Serialize(request, Json);
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
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
