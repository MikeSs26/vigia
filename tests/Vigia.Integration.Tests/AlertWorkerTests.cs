using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vigia.Api.Workers;
using Vigia.Core.Alerting;
using Vigia.Infrastructure.Alerting;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Querying;

namespace Vigia.Integration.Tests;

[Collection("postgres")]
public class AlertWorkerTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Anchor = DateTimeOffset.UtcNow;

    private AlertWorker Worker(FakeTimeProvider time) =>
        new(new PostgresAlertStore(postgres.ConnectionString),
            new PostgresMetricQueryReader(postgres.ConnectionString),
            Options.Create(new AlertOptions()),
            time,
            NullLogger<AlertWorker>.Instance);

    [Fact]
    public async Task ABreachHeldLongEnoughEnqueuesExactlyOneMessage()
    {
        var seed = await AlertingFixture.SeedAsync(postgres, Anchor, threshold: 50, forSeconds: 0);
        await AlertingFixture.WritePointsAsync(postgres, seed.SeriesId, Anchor, value: 90);

        var time = new FakeTimeProvider(Anchor);
        var worker = Worker(time);

        await worker.RunCycleAsync(default);   // Ok -> Pending
        await worker.RunCycleAsync(default);   // Pending -> Firing, notifies

        await using var context = postgres.CreateContext();
        Assert.Equal(1, await context.Outbox.CountAsync(m => m.ChannelId == seed.ChannelId));

        // Staying breached must not produce a second message.
        await worker.RunCycleAsync(default);
        Assert.Equal(1, await context.Outbox.CountAsync(m => m.ChannelId == seed.ChannelId));
    }

    [Fact]
    public async Task ARollupWorkerLeftBehindDoesNotCauseNoData()
    {
        // The regression test for the decision to read raw points. When the rollup
        // worker was first deployed it had twelve days to aggregate and the 1m
        // table held nothing recent for about twelve minutes. A rule reading that
        // table would have declared NoData on every rule at once.
        var seed = await AlertingFixture.SeedAsync(postgres, Anchor, threshold: 50, forSeconds: 0);
        await AlertingFixture.WritePointsAsync(postgres, seed.SeriesId, Anchor, value: 10);

        // metric_rollups_1m is deliberately left empty.
        await Worker(new FakeTimeProvider(Anchor)).RunCycleAsync(default);

        await using var context = postgres.CreateContext();
        var instance = await context.AlertInstances.SingleAsync(i => i.RuleId == seed.RuleId);

        Assert.Equal(AlertState.Ok, instance.State);
    }

    [Fact]
    public async Task ASilentRuleRecordsTheTransitionAndDeliversNothing()
    {
        // A new rule is created with no channel: recording and visible, silent.
        var seed = await AlertingFixture.SeedAsync(
            postgres, Anchor, threshold: 50, forSeconds: 0, withChannel: false);
        await AlertingFixture.WritePointsAsync(postgres, seed.SeriesId, Anchor, value: 90);

        var worker = Worker(new FakeTimeProvider(Anchor));
        await worker.RunCycleAsync(default);
        await worker.RunCycleAsync(default);

        await using var context = postgres.CreateContext();
        var instance = await context.AlertInstances.SingleAsync(i => i.RuleId == seed.RuleId);

        Assert.Equal(AlertState.Firing, instance.State);
        Assert.Equal(0, await context.Outbox.CountAsync(m => m.ChannelId == seed.ChannelId));

        // No suppression layer was responsible — the rule simply has nowhere to
        // send. Stamping a reason here would misreport why nothing was sent.
        var fired = await context.AlertEvents
            .SingleAsync(e => e.InstanceId == instance.Id && e.ToState == AlertState.Firing);

        Assert.Null(fired.SuppressedReason);
    }

    [Fact]
    public async Task EnteringPendingDeliversNothing()
    {
        // `forSeconds` is non-zero so the first cycle stops at Pending. Nothing
        // touching Pending may reach a channel — that silence IS the anti-flapping
        // mechanism, and without this test the guard that enforces it can be
        // deleted: a premature delivery then gets masked, because the real one a
        // cycle later is suppressed by its own cooldown and the net count is
        // unchanged.
        var seed = await AlertingFixture.SeedAsync(postgres, Anchor, threshold: 50, forSeconds: 300);
        await AlertingFixture.WritePointsAsync(postgres, seed.SeriesId, Anchor, value: 90);

        await Worker(new FakeTimeProvider(Anchor)).RunCycleAsync(default);

        await using var context = postgres.CreateContext();
        var instance = await context.AlertInstances.SingleAsync(i => i.RuleId == seed.RuleId);

        Assert.Equal(AlertState.Pending, instance.State);
        Assert.Equal(0, await context.Outbox.CountAsync(m => m.ChannelId == seed.ChannelId));
        Assert.Null(instance.LastNotifiedAt);
    }

    [Fact]
    public async Task ASilencedRuleRecordsWhyItWasNotDelivered()
    {
        // The only test that drives a real suppression through the worker. The
        // silence targets this rule rather than being global, so it cannot leak
        // into whatever other test runs next against the shared database.
        var seed = await AlertingFixture.SeedAsync(postgres, Anchor, threshold: 50, forSeconds: 0);
        await AlertingFixture.WritePointsAsync(postgres, seed.SeriesId, Anchor, value: 90);

        await using (var setup = postgres.CreateContext())
        {
            setup.Silences.Add(new SilenceEntity
            {
                TenantId = seed.TenantId,
                TargetKind = SilenceTarget.Rule,
                TargetId = seed.RuleId,
                Until = Anchor.AddHours(1),
                Reason = "maintenance",
                CreatedBy = "test",
            });
            await setup.SaveChangesAsync();
        }

        var worker = Worker(new FakeTimeProvider(Anchor));
        await worker.RunCycleAsync(default);
        await worker.RunCycleAsync(default);

        await using var context = postgres.CreateContext();
        var instance = await context.AlertInstances.SingleAsync(i => i.RuleId == seed.RuleId);

        Assert.Equal(AlertState.Firing, instance.State);
        Assert.Equal(0, await context.Outbox.CountAsync(m => m.ChannelId == seed.ChannelId));

        var fired = await context.AlertEvents
            .SingleAsync(e => e.InstanceId == instance.Id && e.ToState == AlertState.Firing);

        Assert.Equal(SuppressionReason.Silenced, fired.SuppressedReason);

        // Suppressed means nobody was told, so the cooldown clock must not start.
        Assert.Null(instance.LastNotifiedAt);
    }

    [Fact]
    public async Task AHostThatStopsReportingEntersNoData()
    {
        var seed = await AlertingFixture.SeedAsync(postgres, Anchor, threshold: 50, forSeconds: 0);
        // No points written at all.

        await Worker(new FakeTimeProvider(Anchor)).RunCycleAsync(default);

        await using var context = postgres.CreateContext();
        var instance = await context.AlertInstances.SingleAsync(i => i.RuleId == seed.RuleId);

        Assert.Equal(AlertState.NoData, instance.State);
        Assert.Equal(1, await context.Outbox.CountAsync(m => m.ChannelId == seed.ChannelId));
    }
}
