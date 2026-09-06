using Microsoft.EntityFrameworkCore;

namespace Vigia.Infrastructure.Alerting;

public sealed class PostgresOutboxStore(string connectionString) : IOutboxStore
{
    private VigiaDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<VigiaDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new VigiaDbContext(options);
    }

    public async Task<IReadOnlyList<PendingMessage>> ClaimDueAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        // Two queries rather than a join, deliberately. The alerting tables carry
        // no foreign keys, so a channel can be deleted while its messages remain.
        // An inner join would silently drop those messages from this query: never
        // delivered, never marked failed, and eventually discarded by the size
        // trim as "oldest undelivered" — the message disappears and nothing ever
        // says so. Claiming them with a null channel name lets the worker fail
        // them loudly instead.
        var due = await context.Outbox
            .Where(m => m.SentAt == null && m.FailedAt == null && m.NextAttemptAt <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(limit)
            .Select(m => new { m.Id, m.ChannelId, m.Payload, m.Attempts })
            .ToListAsync(cancellationToken);

        var channelIds = due.Select(m => m.ChannelId).Distinct().ToList();

        var names = await context.NotificationChannels
            .Where(c => channelIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        return due
            .Select(m => new PendingMessage(
                m.Id,
                names.TryGetValue(m.ChannelId, out var name) ? name : null,
                m.Payload,
                m.Attempts))
            .ToList();
    }

    public async Task MarkDeliveredAsync(
        long id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        await context.Outbox
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SentAt, at), cancellationToken);
    }

    public async Task MarkRetryAsync(
        long id, int attempts, DateTimeOffset nextAttemptAt,
        string error, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        await context.Outbox
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                .SetProperty(m => m.LastError, error), cancellationToken);
    }

    public async Task MarkFailedAsync(
        long id, int attempts, DateTimeOffset at, string error, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        await context.Outbox
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Attempts, attempts)
                .SetProperty(m => m.FailedAt, at)
                .SetProperty(m => m.LastError, error), cancellationToken);
    }

    public async Task<int> TrimAsync(int maxRows, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        var undelivered = await context.Outbox
            .Where(m => m.SentAt == null && m.FailedAt == null)
            .CountAsync(cancellationToken);

        var excess = undelivered - maxRows;

        if (excess <= 0)
        {
            return 0;
        }

        // Oldest first: in an incident the newest message is the one worth keeping.
        var doomed = await context.Outbox
            .Where(m => m.SentAt == null && m.FailedAt == null)
            .OrderBy(m => m.CreatedAt)
            .Take(excess)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);

        return await context.Outbox
            .Where(m => doomed.Contains(m.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }
}
