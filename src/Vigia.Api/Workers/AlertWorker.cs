using System.Text.Json;
using Microsoft.Extensions.Options;
using Vigia.Core.Alerting;
using Vigia.Core.Querying;
using Vigia.Infrastructure.Alerting;
using Vigia.Infrastructure.Querying;

namespace Vigia.Api.Workers;

/// <summary>
/// Evaluates every enabled rule and advances its state machine. It never calls
/// Discord: the message goes to the outbox in the same transaction as the state
/// change, so a network failure cannot leave an alert recorded as notified that
/// was never sent.
/// </summary>
public sealed class AlertWorker(
    IAlertStore store,
    IMetricQueryReader reader,
    IOptions<AlertOptions> options,
    TimeProvider timeProvider,
    ILogger<AlertWorker> logger) : BackgroundService
{
    private readonly AlertOptions _options = options.Value;

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
                logger.LogError(ex, "Alert cycle failed; will retry on the next tick");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    internal async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        var targets = await store.LoadTargetsAsync(now, cancellationToken);
        var silences = await store.LoadActiveSilencesAsync(now, cancellationToken);

        foreach (var target in targets)
        {
            await EvaluateAsync(target, silences, now, cancellationToken);
        }
    }

    private async Task EvaluateAsync(
        EvaluationTarget target,
        IReadOnlyList<Silence> silences,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var samples = await ReadSamplesAsync(target, now, cancellationToken);
        var result = AlertEvaluator.Evaluate(target.Rule, target.State, samples, now);

        if (result.Transition is not { } transition || !transition.Notifies)
        {
            await store.CommitAsync(new CommitRequest(
                target.Rule.Id, target.SourceId, result.NewState, now,
                result.Transition, SuppressedReason: null,
                ChannelId: null, Payload: null), cancellationToken);
            return;
        }

        // A rule with no channel is dashboard-only: the transition is recorded,
        // nothing is delivered, and no suppression layer was responsible.
        if (target.ChannelId is not { } channelId)
        {
            await store.CommitAsync(new CommitRequest(
                target.Rule.Id, target.SourceId, result.NewState, now,
                transition, SuppressedReason: null,
                ChannelId: null, Payload: null), cancellationToken);
            return;
        }

        var channel = await store.LoadChannelAsync(channelId, cancellationToken);

        var reason = NotificationDecider.Decide(
            new NotificationContext(
                transition, target.Rule.Severity, channel,
                target.Rule.Id, target.SourceId,
                target.LastNotifiedAt, target.Rule.Cooldown),
            silences,
            now);

        var payload = reason == SuppressionReason.None
            ? Compose(target, transition)
            : null;

        await store.CommitAsync(new CommitRequest(
            target.Rule.Id, target.SourceId, result.NewState, now,
            transition,
            reason == SuppressionReason.None ? null : reason,
            payload is null ? null : channelId,
            payload), cancellationToken);
    }

    private async Task<IReadOnlyList<Sample>> ReadSamplesAsync(
        EvaluationTarget target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Raw points, always. Reading the 1-minute rollups would make the alert
        // engine wrong whenever the rollup worker is behind — a catch-up would
        // declare NoData on every rule at once.
        var span = target.Rule.Window > target.Rule.NoDataAfter
            ? target.Rule.Window
            : target.Rule.NoDataAfter;

        if (span > _options.MaxRuleWindow)
        {
            span = _options.MaxRuleWindow;
        }

        var results = await reader.ReadAsync(
            new MetricQuery(
                target.TenantId, target.SourceId, target.MetricName,
                now - span, now, Granularity.Raw, Aggregation.Avg),
            cancellationToken);

        return results
            .SelectMany(r => r.Points)
            .Select(p => new Sample(p.Ts, p.Value))
            .ToList();
    }

    private static string Compose(EvaluationTarget target, AlertTransition transition)
    {
        var verb = transition.To switch
        {
            AlertState.Firing => "FIRING",
            AlertState.NoData => "NO DATA",
            _ => "RESOLVED",
        };

        var value = transition.Value is { } v ? $" (value {v:0.##})" : string.Empty;

        return JsonSerializer.Serialize(new
        {
            content = $"**{verb}** `{target.MetricName}` on source {target.SourceId}{value}",
        });
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
