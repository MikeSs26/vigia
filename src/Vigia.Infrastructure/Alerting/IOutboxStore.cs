namespace Vigia.Infrastructure.Alerting;

/// <summary>
/// <paramref name="ChannelName"/> is null when the channel this message was queued
/// for no longer exists. There is no foreign key from the outbox to the channel
/// table, so that is reachable state, and it must be surfaced rather than filtered
/// away — see the claim query for why.
/// </summary>
public readonly record struct PendingMessage(
    long Id, string? ChannelName, string Payload, int Attempts);

public interface IOutboxStore
{
    Task<IReadOnlyList<PendingMessage>> ClaimDueAsync(
        DateTimeOffset now, int limit, CancellationToken cancellationToken);

    Task MarkDeliveredAsync(long id, DateTimeOffset at, CancellationToken cancellationToken);

    Task MarkRetryAsync(
        long id, int attempts, DateTimeOffset nextAttemptAt,
        string error, CancellationToken cancellationToken);

    Task MarkFailedAsync(
        long id, int attempts, DateTimeOffset at, string error, CancellationToken cancellationToken);

    /// <summary>
    /// Drops the oldest undelivered rows beyond <paramref name="maxRows"/>.
    /// Returns how many were dropped.
    /// </summary>
    Task<int> TrimAsync(int maxRows, CancellationToken cancellationToken);
}
