namespace Vigia.Infrastructure.Notifications;

public enum PublishOutcome
{
    Delivered,
    Retry,
    Rejected,

    /// <summary>
    /// Discord refused for now, not for good. Distinct from <see cref="Retry"/>
    /// because it must not consume the message's attempt budget.
    /// </summary>
    RateLimited,
}

public interface IWebhookPublisher
{
    Task<PublishOutcome> PublishAsync(
        string url, string payload, CancellationToken cancellationToken);
}
