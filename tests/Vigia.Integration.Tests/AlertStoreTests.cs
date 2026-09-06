using Microsoft.EntityFrameworkCore;
using Vigia.Core.Alerting;
using Vigia.Infrastructure.Alerting;
using Vigia.Infrastructure.Entities;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class AlertStoreTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Anchor =
        new(2032, 3, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Seeds a tenant, a source, an optional channel and a rule, and returns all
    /// three ids. The channel belongs to the SAME tenant as the rule, and its real
    /// id is returned rather than assumed: the alerting tables carry no foreign
    /// keys, so a hardcoded channel id would insert happily and the test would pass
    /// for the wrong reason.
    /// </summary>
    private async Task<(int RuleId, int SourceId, int? ChannelId)> SeedRuleAsync(bool withChannel = false)
    {
        await using var context = postgres.CreateContext();

        var tenant = new Tenant { Name = "T", Slug = $"t-{Guid.NewGuid():N}", CreatedAt = Anchor };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        int? channelId = null;
        if (withChannel)
        {
            var channel = new NotificationChannelEntity
            {
                TenantId = tenant.Id, Kind = "discord_webhook",
                Name = $"c-{Guid.NewGuid():N}", MinSeverity = Severity.Info, Enabled = true,
            };

            context.NotificationChannels.Add(channel);
            await context.SaveChangesAsync();
            channelId = channel.Id;
        }

        var source = new Source
        {
            TenantId = tenant.Id, Name = $"h-{Guid.NewGuid():N}", Kind = SourceKind.Host,
        };
        context.Sources.Add(source);
        await context.SaveChangesAsync();

        var rule = new AlertRuleEntity
        {
            TenantId = tenant.Id,
            SourceId = source.Id,
            MetricName = "cpu.usage",
            Aggregation = RuleAggregation.Avg,
            WindowSeconds = 300,
            Operator = ComparisonOperator.Gt,
            Threshold = 85,
            ForSeconds = 300,
            NoDataAfterSeconds = 120,
            Severity = Severity.Warning,
            ChannelId = channelId,
            CooldownSeconds = 1800,
            Enabled = true,
        };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();

        return (rule.Id, source.Id, channelId);
    }

    private PostgresAlertStore Store() => new(postgres.ConnectionString);

    [Fact]
    public async Task ADisabledRuleIsNotLoaded()
    {
        var (ruleId, _, _) = await SeedRuleAsync();

        await using (var context = postgres.CreateContext())
        {
            var rule = await context.AlertRules.SingleAsync(r => r.Id == ruleId);
            rule.Enabled = false;
            await context.SaveChangesAsync();
        }

        var targets = await Store().LoadTargetsAsync(Anchor, default);

        Assert.DoesNotContain(targets, t => t.Rule.Id == ruleId);
    }

    [Fact]
    public async Task AFreshRuleLoadsWithAnOkStateRatherThanNothing()
    {
        // A rule that has never been evaluated must still be evaluated. Starting
        // from Ok is what lets the first breach produce a transition.
        var (ruleId, sourceId, _) = await SeedRuleAsync();

        var target = (await Store().LoadTargetsAsync(Anchor, default))
            .Single(t => t.Rule.Id == ruleId);

        Assert.Equal(AlertState.Ok, target.State.State);
        Assert.Equal(sourceId, target.SourceId);
        Assert.Equal(TimeSpan.FromSeconds(300), target.Rule.Window);
    }

    [Fact]
    public async Task CommittingATransitionWritesStateEventAndOutboxTogether()
    {
        var (ruleId, sourceId, channelId) = await SeedRuleAsync(withChannel: true);

        await Store().CommitAsync(new CommitRequest(
            ruleId, sourceId,
            new AlertInstanceState(AlertState.Firing, Anchor, 90.0),
            Anchor,
            new AlertTransition(AlertState.Pending, AlertState.Firing, Anchor, 90.0),
            SuppressedReason: null,
            ChannelId: channelId,
            Payload: """{"content":"fired"}"""), default);

        await using var context = postgres.CreateContext();

        var instance = await context.AlertInstances.SingleAsync(i => i.RuleId == ruleId);
        Assert.Equal(AlertState.Firing, instance.State);
        Assert.Equal(Anchor, instance.LastNotifiedAt);

        Assert.Equal(1, await context.AlertEvents.CountAsync(e => e.InstanceId == instance.Id));

        // Scoped to this test's own channel. The fixture database is shared with
        // every other test class, so a global count would drift with whatever
        // they happen to have left behind.
        Assert.Equal(1, await context.Outbox.CountAsync(m => m.ChannelId == channelId));
    }

    [Fact]
    public async Task ASuppressedTransitionRecordsTheReasonAndEnqueuesNothing()
    {
        var (ruleId, sourceId, _) = await SeedRuleAsync();

        // No channel to scope a count by, so measure the delta instead: the
        // fixture database is shared, and an absolute zero would be asserting
        // something about every other test class rather than about this commit.
        int before;
        await using (var probe = postgres.CreateContext())
        {
            before = await probe.Outbox.CountAsync();
        }

        await Store().CommitAsync(new CommitRequest(
            ruleId, sourceId,
            new AlertInstanceState(AlertState.Firing, Anchor, 90.0),
            Anchor,
            new AlertTransition(AlertState.Pending, AlertState.Firing, Anchor, 90.0),
            SuppressedReason: SuppressionReason.KillSwitch,
            ChannelId: null,
            Payload: null), default);

        await using var context = postgres.CreateContext();

        var evt = await context.AlertEvents
            .SingleAsync(e => e.InstanceId == context.AlertInstances
                .Single(i => i.RuleId == ruleId).Id);

        Assert.Equal(SuppressionReason.KillSwitch, evt.SuppressedReason);
        Assert.Equal(before, await context.Outbox.CountAsync());

        // Suppressed means nobody was told, so the cooldown clock must not start.
        var instance = await context.AlertInstances.SingleAsync(i => i.RuleId == ruleId);
        Assert.Null(instance.LastNotifiedAt);
    }

    [Fact]
    public async Task AFailureWritingTheOutboxRollsBackTheStateChange()
    {
        // The promise this whole class exists for. `payload` is a jsonb column, so
        // text that is not JSON is refused by PostgreSQL — and the refusal lands
        // AFTER the instance has been flushed inside the transaction, which is
        // exactly the window a two-transaction implementation would leak through.
        var (ruleId, sourceId, channelId) = await SeedRuleAsync(withChannel: true);

        await Assert.ThrowsAnyAsync<Exception>(() => Store().CommitAsync(new CommitRequest(
            ruleId, sourceId,
            new AlertInstanceState(AlertState.Firing, Anchor, 90.0),
            Anchor,
            new AlertTransition(AlertState.Pending, AlertState.Firing, Anchor, 90.0),
            SuppressedReason: null,
            ChannelId: channelId,
            Payload: "this is not json"), default));

        await using var context = postgres.CreateContext();

        // Nothing survived: not the instance, not the event, not the outbox row.
        Assert.False(await context.AlertInstances.AnyAsync(i => i.RuleId == ruleId));
        Assert.Equal(0, await context.Outbox.CountAsync(m => m.ChannelId == channelId));
    }

    [Fact]
    public async Task ARuleWithNoSourceProducesOneTargetPerSourceOfItsTenant()
    {
        // What makes NoData useful across a fleet: adding a host must not require
        // editing rules.
        await using var context = postgres.CreateContext();

        var tenant = new Tenant { Name = "F", Slug = $"f-{Guid.NewGuid():N}", CreatedAt = Anchor };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var first = new Source
        {
            TenantId = tenant.Id, Name = $"h-{Guid.NewGuid():N}", Kind = SourceKind.Host,
        };
        var second = new Source
        {
            TenantId = tenant.Id, Name = $"h-{Guid.NewGuid():N}", Kind = SourceKind.Host,
        };
        context.Sources.AddRange(first, second);
        await context.SaveChangesAsync();

        var rule = new AlertRuleEntity
        {
            TenantId = tenant.Id,
            SourceId = null,
            MetricName = "cpu.usage",
            Aggregation = RuleAggregation.Avg,
            WindowSeconds = 300,
            Operator = ComparisonOperator.Gt,
            Threshold = 85,
            ForSeconds = 300,
            NoDataAfterSeconds = 120,
            Severity = Severity.Warning,
            CooldownSeconds = 1800,
            Enabled = true,
        };
        context.AlertRules.Add(rule);
        await context.SaveChangesAsync();

        var targets = (await Store().LoadTargetsAsync(Anchor, default))
            .Where(t => t.Rule.Id == rule.Id)
            .ToList();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.SourceId == first.Id);
        Assert.Contains(targets, t => t.SourceId == second.Id);
    }

    [Fact]
    public async Task AChannelThatNoLongerExistsLoadsAsDisabledRatherThanThrowing()
    {
        // There is no foreign key from alert_rules.channel_id to
        // notification_channels, so a rule pointing at a deleted channel is
        // reachable state. Throwing here would take the alert worker down on
        // every cycle, every 30 seconds, for one stale row.
        var channel = await Store().LoadChannelAsync(int.MaxValue, default);

        Assert.False(channel.Enabled);
    }

    [Fact]
    public async Task CommittingTwiceForTheSameInstanceUpdatesRatherThanDuplicates()
    {
        var (ruleId, sourceId, _) = await SeedRuleAsync();

        for (var i = 0; i < 2; i++)
        {
            await Store().CommitAsync(new CommitRequest(
                ruleId, sourceId,
                new AlertInstanceState(AlertState.Ok, Anchor, 5.0),
                Anchor.AddSeconds(i * 30),
                Transition: null, SuppressedReason: null,
                ChannelId: null, Payload: null), default);
        }

        await using var context = postgres.CreateContext();
        Assert.Equal(1, await context.AlertInstances.CountAsync(i => i.RuleId == ruleId));
    }

    [Fact]
    public async Task OnlyUnexpiredSilencesAreLoaded()
    {
        await using (var context = postgres.CreateContext())
        {
            var tenant = new Tenant { Name = "S", Slug = $"s-{Guid.NewGuid():N}", CreatedAt = Anchor };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();

            context.Silences.AddRange(
                new SilenceEntity
                {
                    TenantId = tenant.Id, TargetKind = SilenceTarget.Global,
                    Until = Anchor.AddHours(1), Reason = "live", CreatedBy = "cli",
                },
                new SilenceEntity
                {
                    TenantId = tenant.Id, TargetKind = SilenceTarget.Rule, TargetId = 1,
                    Until = Anchor.AddHours(-1), Reason = "expired", CreatedBy = "cli",
                });
            await context.SaveChangesAsync();
        }

        var silences = await Store().LoadActiveSilencesAsync(Anchor, default);

        Assert.Contains(silences, s => s.Target == SilenceTarget.Global);
        Assert.DoesNotContain(silences, s => s.Target == SilenceTarget.Rule);
    }

}
