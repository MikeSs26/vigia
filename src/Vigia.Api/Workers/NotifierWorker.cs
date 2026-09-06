using Microsoft.Extensions.Options;
using Vigia.Infrastructure.Alerting;
using Vigia.Infrastructure.Notifications;

namespace Vigia.Api.Workers;

/// <summary>
/// Drains the outbox. Kept apart from evaluation so that an unreachable Discord
/// accumulates messages and drains on recovery, instead of stalling the alert
/// engine behind an HTTP call.
/// </summary>
public sealed class NotifierWorker(
    IOutboxStore store,
    IWebhookPublisher publisher,
    IOptions<NotifierOptions> options,
    TimeProvider timeProvider,
    ILogger<NotifierWorker> logger) : BackgroundService
{
    private readonly NotifierOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.IntervalSeconds), timeProvider);

        do
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notifier cycle failed; will retry on the next tick");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var dropped = await store.TrimAsync(_options.MaxOutboxRows, now, cancellationToken);

        if (dropped > 0)
        {
            logger.LogWarning(
                "Outbox exceeded {Max} undelivered rows; marked the {Dropped} oldest failed",
                _options.MaxOutboxRows, dropped);
        }

        var due = await store.ClaimDueAsync(now, _options.BatchSize, cancellationToken);

        foreach (var message in due)
        {
            await SendAsync(message, now, cancellationToken);
        }
    }

    private async Task SendAsync(
        PendingMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (message.ChannelName is null)
        {
            // The channel was deleted while this message waited. Fail it with a
            // reason rather than leaving it to be trimmed away unremarked.
            await store.MarkFailedAsync(
                message.Id, message.Attempts, now,
                "The channel this message was queued for no longer exists", cancellationToken);
            return;
        }

        if (!_options.ChannelWebhooks.TryGetValue(message.ChannelName, out var url))
        {
            // The URL lives in the environment, never the database. A channel with
            // no entry is a misconfiguration, not a transient fault.
            await store.MarkFailedAsync(
                message.Id, message.Attempts, now,
                $"No webhook configured for channel '{message.ChannelName}'", cancellationToken);
            return;
        }

        var outcome = await publisher.PublishAsync(url, message.Payload, cancellationToken);
        var attempts = message.Attempts + 1;

        switch (outcome)
        {
            case PublishOutcome.Delivered:
                await store.MarkDeliveredAsync(message.Id, now, cancellationToken);
                break;

            case PublishOutcome.Rejected:
                await store.MarkFailedAsync(
                    message.Id, attempts, now, "Permanently refused", cancellationToken);
                break;

            case PublishOutcome.RateLimited:
                // Note the UNCHANGED attempt count. Being rate limited is not a
                // failure of the message, so it must not consume the message's
                // budget: a backlog drain outruns Discord's per-webhook limit by
                // design, and charging those refusals would fail every alert in the
                // queue during the outage the outbox exists to survive. What still
                // bounds accumulation is the outbox size limit, not the attempt cap.
                await store.MarkRetryAsync(
                    message.Id, message.Attempts, now + RateLimitDelay,
                    "Rate limited by Discord", cancellationToken);
                break;

            default:
                if (attempts >= _options.MaxAttempts)
                {
                    await store.MarkFailedAsync(
                        message.Id, attempts, now,
                        $"Giving up after {attempts} attempts", cancellationToken);
                    break;
                }

                await store.MarkRetryAsync(
                    message.Id, attempts, now + Backoff(attempts),
                    "Transient failure", cancellationToken);
                break;
        }
    }

    /// <summary>
    /// Fixed, not exponential: a rate limit clears on a wall clock, and the attempt
    /// count this backoff would key off deliberately does not move while it holds.
    /// </summary>
    private static readonly TimeSpan RateLimitDelay = TimeSpan.FromSeconds(60);

    /// <summary>Exponential, capped: 2s, 4s, 8s ... to a ceiling of 30 minutes.</summary>
    private static TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(Math.Pow(2, attempts), 1800);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task<bool> WaitAsync(
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
