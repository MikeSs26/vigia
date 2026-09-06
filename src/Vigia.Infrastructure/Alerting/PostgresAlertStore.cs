using Microsoft.EntityFrameworkCore;
using Vigia.Core.Alerting;
using Vigia.Infrastructure.Entities;

namespace Vigia.Infrastructure.Alerting;

public sealed class PostgresAlertStore(string connectionString) : IAlertStore
{
    private VigiaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<VigiaDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new VigiaDbContext(options);
    }

    public async Task<IReadOnlyList<EvaluationTarget>> LoadTargetsAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        var rules = await context.AlertRules
            .Where(r => r.Enabled)
            .ToListAsync(cancellationToken);

        var sources = await context.Sources
            .Select(s => new { s.Id, s.TenantId })
            .ToListAsync(cancellationToken);

        var instances = await context.AlertInstances.ToListAsync(cancellationToken);

        var targets = new List<EvaluationTarget>();

        foreach (var rule in rules)
        {
            // A rule with no source targets every source of its tenant, which is
            // what makes NoData useful across a fleet without editing rules when
            // a host is added.
            var matching = rule.SourceId is { } only
                ? sources.Where(s => s.Id == only)
                : sources.Where(s => s.TenantId == rule.TenantId);

            foreach (var source in matching)
            {
                var instance = instances.SingleOrDefault(
                    i => i.RuleId == rule.Id && i.SourceId == source.Id);

                targets.Add(new EvaluationTarget(
                    Rule: ToDomain(rule),
                    MetricName: rule.MetricName,
                    TenantId: rule.TenantId,
                    SourceId: source.Id,
                    ChannelId: rule.ChannelId,
                    // Never evaluated yet: start from Ok so the first breach is a
                    // transition rather than an invisible jump into Firing.
                    State: instance is null
                        ? new AlertInstanceState(AlertState.Ok, now, null)
                        : new AlertInstanceState(instance.State, instance.StateSince, instance.LastValue),
                    LastNotifiedAt: instance?.LastNotifiedAt));
            }
        }

        return targets;
    }

    public async Task<IReadOnlyList<Silence>> LoadActiveSilencesAsync(
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        return await context.Silences
            .Where(s => s.Until > now)
            .Select(s => new Silence(s.TargetKind, s.TargetId, s.Until))
            .ToListAsync(cancellationToken);
    }

    public async Task<NotificationChannel> LoadChannelAsync(
        int channelId, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        var channel = await context.NotificationChannels
            .SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken);

        // A channel deleted out from under a rule is treated as disabled rather
        // than as an error: alerting degrades, ingestion does not stop.
        return channel is null
            ? new NotificationChannel(channelId, Severity.Critical, Enabled: false)
            : new NotificationChannel(channel.Id, channel.MinSeverity, channel.Enabled);
    }

    public async Task CommitAsync(CommitRequest request, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var instance = await context.AlertInstances.SingleOrDefaultAsync(
            i => i.RuleId == request.RuleId && i.SourceId == request.SourceId,
            cancellationToken);

        if (instance is null)
        {
            instance = new AlertInstance { RuleId = request.RuleId, SourceId = request.SourceId };
            context.AlertInstances.Add(instance);
        }

        instance.State = request.NewState.State;
        instance.StateSince = request.NewState.StateSince;
        instance.LastValue = request.NewState.LastValue;
        instance.LastEvaluatedAt = request.EvaluatedAt;

        if (request.NewState.State == AlertState.Firing && instance.FiredAt is null)
        {
            instance.FiredAt = request.NewState.StateSince;
        }
        else if (request.NewState.State != AlertState.Firing)
        {
            instance.FiredAt = null;
        }

        var delivering = request.Payload is not null && request.ChannelId is not null;

        if (delivering)
        {
            // Only a delivered notification starts the cooldown clock. Starting it
            // on a suppressed one would let a single suppression hide the next
            // genuine message too.
            instance.LastNotifiedAt = request.EvaluatedAt;
        }

        // The instance id is needed by the event row, so flush inside the
        // transaction rather than committing twice.
        await context.SaveChangesAsync(cancellationToken);

        if (request.Transition is { } transition)
        {
            context.AlertEvents.Add(new AlertEvent
            {
                InstanceId = instance.Id,
                FromState = transition.From,
                ToState = transition.To,
                At = transition.At,
                Value = transition.Value,
                SuppressedReason = request.SuppressedReason,
            });
        }

        if (delivering)
        {
            context.Outbox.Add(new OutboxMessage
            {
                ChannelId = request.ChannelId!.Value,
                Payload = request.Payload!,
                CreatedAt = request.EvaluatedAt,
                Attempts = 0,
                NextAttemptAt = request.EvaluatedAt,
            });
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static AlertRule ToDomain(AlertRuleEntity rule) => new(
        Id: rule.Id,
        Aggregation: rule.Aggregation,
        Window: TimeSpan.FromSeconds(rule.WindowSeconds),
        Operator: rule.Operator,
        Threshold: rule.Threshold,
        For: TimeSpan.FromSeconds(rule.ForSeconds),
        NoDataAfter: TimeSpan.FromSeconds(rule.NoDataAfterSeconds),
        Severity: rule.Severity,
        Cooldown: TimeSpan.FromSeconds(rule.CooldownSeconds));
}
