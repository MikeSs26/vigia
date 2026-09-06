using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vigia.Api.Workers;
using Vigia.Infrastructure.Alerting;
using Vigia.Infrastructure.Entities;
using Vigia.Infrastructure.Notifications;

namespace Vigia.Integration.Tests;

file sealed class StubPublisher(PublishOutcome outcome) : IWebhookPublisher
{
    public int Calls { get; private set; }

    public Task<PublishOutcome> PublishAsync(string url, string payload, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(outcome);
    }
}

[Collection("postgres")]
public class NotifierWorkerTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Anchor = new(2032, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<(int ChannelId, long MessageId)> SeedMessageAsync()
    {
        await using var context = postgres.CreateContext();

        // The outbox bound counts undelivered rows across the whole table, and
        // this fixture's database is shared with every other test class. Leftover
        // rows would make the trim drop a different set each run, so start clean.
        await context.Outbox.ExecuteDeleteAsync();

        var tenant = new Tenant { Name = "N", Slug = $"n-{Guid.NewGuid():N}", CreatedAt = Anchor };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync();

        var channel = new NotificationChannelEntity
        {
            TenantId = tenant.Id,
            Kind = "discord_webhook",
            Name = "ops",
            MinSeverity = Vigia.Core.Alerting.Severity.Info,
            Enabled = true,
        };
        context.NotificationChannels.Add(channel);
        await context.SaveChangesAsync();

        var message = new OutboxMessage
        {
            ChannelId = channel.Id,
            Payload = """{"content":"x"}""",
            CreatedAt = Anchor,
            Attempts = 0,
            NextAttemptAt = Anchor,
        };
        context.Outbox.Add(message);
        await context.SaveChangesAsync();

        return (channel.Id, message.Id);
    }

    private NotifierWorker Worker(IWebhookPublisher publisher, FakeTimeProvider time) =>
        new(new PostgresOutboxStore(postgres.ConnectionString),
            publisher,
            Options.Create(new NotifierOptions
            {
                ChannelWebhooks = { ["ops"] = "https://example.invalid/hook" },
                MaxAttempts = 3,
                MaxOutboxRows = 5,
            }),
            time,
            NullLogger<NotifierWorker>.Instance);

    [Fact]
    public async Task ADeliveredMessageIsMarkedSentAndNotSentAgain()
    {
        var (_, messageId) = await SeedMessageAsync();
        var publisher = new StubPublisher(PublishOutcome.Delivered);
        var worker = Worker(publisher, new FakeTimeProvider(Anchor));

        await worker.RunCycleAsync(default);
        await worker.RunCycleAsync(default);

        Assert.Equal(1, publisher.Calls);

        await using var context = postgres.CreateContext();
        var message = await context.Outbox.SingleAsync(m => m.Id == messageId);
        Assert.NotNull(message.SentAt);
    }

    [Fact]
    public async Task ATransientFailureBacksOffRatherThanRetryingImmediately()
    {
        var (_, messageId) = await SeedMessageAsync();
        var worker = Worker(new StubPublisher(PublishOutcome.Retry), new FakeTimeProvider(Anchor));

        await worker.RunCycleAsync(default);

        await using var context = postgres.CreateContext();
        var message = await context.Outbox.SingleAsync(m => m.Id == messageId);

        Assert.Equal(1, message.Attempts);
        Assert.True(message.NextAttemptAt > Anchor);
        Assert.Null(message.SentAt);
    }

    [Fact]
    public async Task APermanentRefusalIsNotRetried()
    {
        var (_, messageId) = await SeedMessageAsync();
        var publisher = new StubPublisher(PublishOutcome.Rejected);
        var worker = Worker(publisher, new FakeTimeProvider(Anchor));

        await worker.RunCycleAsync(default);
        await worker.RunCycleAsync(default);

        Assert.Equal(1, publisher.Calls);

        await using var context = postgres.CreateContext();
        var message = await context.Outbox.SingleAsync(m => m.Id == messageId);
        Assert.NotNull(message.FailedAt);
    }

    [Fact]
    public async Task TheAttemptCapStopsRetryingForever()
    {
        var (_, messageId) = await SeedMessageAsync();
        var time = new FakeTimeProvider(Anchor);
        var worker = Worker(new StubPublisher(PublishOutcome.Retry), time);

        for (var i = 0; i < 4; i++)
        {
            await worker.RunCycleAsync(default);
            time.SetUtcNow(time.GetUtcNow().AddHours(1));
        }

        await using var context = postgres.CreateContext();
        var message = await context.Outbox.SingleAsync(m => m.Id == messageId);

        Assert.NotNull(message.FailedAt);
        Assert.Equal(3, message.Attempts);
    }

    [Fact]
    public async Task SustainedRateLimitingNeverPermanentlyFailsAMessage()
    {
        // Recovering from a long outage means draining a backlog, and draining it at
        // BatchSize every interval outruns Discord's per-webhook limit — so the
        // recovery rate-limits itself. If 429 consumed the attempt budget, the outbox
        // would permanently discard alerts during precisely the scenario it exists to
        // survive. Being told "not now" is not a failure of the message.
        var (_, messageId) = await SeedMessageAsync();
        var time = new FakeTimeProvider(Anchor);
        var publisher = new StubPublisher(PublishOutcome.RateLimited);
        var worker = Worker(publisher, time);

        // MaxAttempts is 3 here; run well past it, clearing the 60s delay each time.
        for (var i = 0; i < 10; i++)
        {
            await worker.RunCycleAsync(default);
            time.SetUtcNow(time.GetUtcNow().AddSeconds(61));
        }

        Assert.Equal(10, publisher.Calls);

        await using var context = postgres.CreateContext();
        var message = await context.Outbox.SingleAsync(m => m.Id == messageId);

        Assert.Null(message.FailedAt);
        Assert.Null(message.SentAt);

        // The budget was never spent: rate limiting says nothing about the message.
        Assert.Equal(0, message.Attempts);
        Assert.Contains("Rate limited", message.LastError);
    }

    [Fact]
    public async Task ABackedOffMessageIsNotRetriedOnTheNextCycleAtTheSameInstant()
    {
        // Backoff only means anything if the claim honours it. Without the
        // due-date predicate the drain would hammer a failing webhook on every
        // cycle instead of waiting out the delay it had just set for itself.
        var (_, messageId) = await SeedMessageAsync();

        var publisher = new StubPublisher(PublishOutcome.Retry);
        var worker = Worker(publisher, new FakeTimeProvider(Anchor));

        await worker.RunCycleAsync(default);
        Assert.Equal(1, publisher.Calls);

        // Same instant, and the message is now scheduled into the future.
        await worker.RunCycleAsync(default);

        Assert.Equal(1, publisher.Calls);

        await using var context = postgres.CreateContext();
        var message = await context.Outbox.SingleAsync(m => m.Id == messageId);

        Assert.Equal(1, message.Attempts);
    }

    [Fact]
    public async Task TheOutboxBoundNeverDropsDeliveredHistory()
    {
        // The bound exists to stop UNDELIVERED messages filling the disk during an
        // outage. Delivered rows are the record of what was actually sent, and a
        // trim that reached them would erase that record to make room.
        var (channelId, _) = await SeedMessageAsync();

        long deliveredId;
        await using (var setup = postgres.CreateContext())
        {
            var delivered = new OutboxMessage
            {
                ChannelId = channelId,
                Payload = """{"content":"already sent"}""",
                CreatedAt = Anchor.AddYears(-1),
                Attempts = 1,
                NextAttemptAt = Anchor,
                SentAt = Anchor,
            };
            setup.Outbox.Add(delivered);

            for (var i = 1; i <= 9; i++)
            {
                setup.Outbox.Add(new OutboxMessage
                {
                    ChannelId = channelId,
                    Payload = $$"""{"content":"{{i}}"}""",
                    CreatedAt = Anchor.AddMinutes(i),
                    Attempts = 0,
                    NextAttemptAt = Anchor.AddYears(1),
                });
            }

            await setup.SaveChangesAsync();
            deliveredId = delivered.Id;
        }

        await Worker(new StubPublisher(PublishOutcome.Retry), new FakeTimeProvider(Anchor))
            .RunCycleAsync(default);

        await using var context = postgres.CreateContext();

        // It is by far the oldest row in the table, so a trim that ignored
        // delivery status would take it first.
        Assert.True(await context.Outbox.AnyAsync(m => m.Id == deliveredId));
    }

    [Fact]
    public async Task AMessageWhoseChannelWasDeletedFailsLoudlyInsteadOfVanishing()
    {
        // There is no foreign key from notification_outbox.channel_id to
        // notification_channels.id, so a deleted channel leaves its messages
        // behind. Claiming them with an inner join would drop them from the query
        // entirely: never delivered, never failed, and eventually discarded by the
        // size trim as "oldest undelivered" — gone, with nothing to show why.
        var (channelId, messageId) = await SeedMessageAsync();

        await using (var setup = postgres.CreateContext())
        {
            await setup.NotificationChannels
                .Where(c => c.Id == channelId)
                .ExecuteDeleteAsync();
        }

        var publisher = new StubPublisher(PublishOutcome.Delivered);
        await Worker(publisher, new FakeTimeProvider(Anchor)).RunCycleAsync(default);

        await using var context = postgres.CreateContext();
        var message = await context.Outbox.SingleAsync(m => m.Id == messageId);

        Assert.NotNull(message.FailedAt);
        Assert.Contains("no longer exists", message.LastError);

        // And nothing was posted for it.
        Assert.Equal(0, publisher.Calls);
    }

    [Fact]
    public async Task TheOutboxBoundMarksTheOldestUndeliveredFailedAndKeepsTheNewest()
    {
        // A Discord outage lasting days must not fill the disk — the same failure
        // the agent's spool bound exists to prevent. In an incident the newest
        // message is the one worth keeping.
        //
        // But the excess must be MARKED FAILED, not deleted. By the time a message
        // reaches the outbox, CommitAsync has already stamped last_notified_at and
        // written an alert_events row with suppressed_reason NULL — correct at
        // enqueue time, because the message was durably queued. Deleting the row
        // afterwards leaves that state reading as "delivered": an alert recorded as
        // notified that was never sent, with nothing left to show it existed.
        var (channelId, _) = await SeedMessageAsync();

        await using (var context = postgres.CreateContext())
        {
            for (var i = 1; i <= 9; i++)
            {
                context.Outbox.Add(new OutboxMessage
                {
                    ChannelId = channelId,
                    Payload = $$"""{"content":"{{i}}"}""",
                    CreatedAt = Anchor.AddMinutes(i),
                    Attempts = 0,
                    NextAttemptAt = Anchor.AddYears(1),
                });
            }

            await context.SaveChangesAsync();
        }

        await Worker(new StubPublisher(PublishOutcome.Retry), new FakeTimeProvider(Anchor))
            .RunCycleAsync(default);

        await using var check = postgres.CreateContext();
        var rows = await check.Outbox
            .Where(m => m.ChannelId == channelId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        // Ten rows in, ten rows out: nothing was erased.
        Assert.Equal(10, rows.Count);

        // MaxOutboxRows is 5, so the five oldest are the excess.
        foreach (var dropped in rows.Take(5))
        {
            Assert.NotNull(dropped.FailedAt);
            Assert.Contains("exceeded", dropped.LastError);
        }

        // The five newest are untouched and still awaiting delivery.
        foreach (var kept in rows.Skip(5))
        {
            Assert.Null(kept.FailedAt);
            Assert.Null(kept.SentAt);
        }

        Assert.Equal(Anchor.AddMinutes(9), rows[^1].CreatedAt);
    }
}
