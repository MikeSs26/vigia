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
            TenantId = tenant.Id, Kind = "discord_webhook", Name = "ops",
            MinSeverity = Vigia.Core.Alerting.Severity.Info, Enabled = true,
        };
        context.NotificationChannels.Add(channel);
        await context.SaveChangesAsync();

        var message = new OutboxMessage
        {
            ChannelId = channel.Id, Payload = """{"content":"x"}""",
            CreatedAt = Anchor, Attempts = 0, NextAttemptAt = Anchor,
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
    public async Task TheOutboxBoundDropsTheOldestUndeliveredAndKeepsTheNewest()
    {
        // A Discord outage lasting days must not fill the disk — the same failure
        // the agent's spool bound exists to prevent. In an incident the newest
        // message is the one worth keeping.
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
        var remaining = await check.Outbox
            .Where(m => m.ChannelId == channelId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync();

        Assert.Equal(5, remaining.Count);
        Assert.Equal(Anchor.AddMinutes(9), remaining[^1].CreatedAt);
    }
}
