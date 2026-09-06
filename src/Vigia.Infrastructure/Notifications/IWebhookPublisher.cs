namespace Vigia.Infrastructure.Notifications;

public enum PublishOutcome
{
    Delivered,
    Retry,
    Rejected,
}

public interface IWebhookPublisher
{
    Task<PublishOutcome> PublishAsync(
        string url, string payload, CancellationToken cancellationToken);
}
