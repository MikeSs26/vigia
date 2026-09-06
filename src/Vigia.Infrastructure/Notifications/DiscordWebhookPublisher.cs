using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vigia.Infrastructure.Notifications;

public sealed class DiscordWebhookPublisher(
    HttpClient client,
    ILogger<DiscordWebhookPublisher>? logger = null) : IWebhookPublisher
{
    private readonly ILogger<DiscordWebhookPublisher> _logger =
        logger ?? NullLogger<DiscordWebhookPublisher>.Instance;

    public async Task<PublishOutcome> PublishAsync(
        string url, string payload, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, content, cancellationToken);

            return WebhookOutcomeClassifier.Classify(response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout, a DNS failure or a torn connection is transient by
            // default. The outbox's attempt cap is what stops this retrying forever.
            _logger.LogWarning(ex, "Webhook publish failed; will retry");
            return PublishOutcome.Retry;
        }
    }
}
