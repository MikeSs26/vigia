using System.Text;
using Microsoft.Extensions.Logging;

namespace Vigia.Agent.Publishing;

public sealed class HttpBatchPublisher(
    HttpClient client,
    ILogger<HttpBatchPublisher> logger) : IBatchPublisher
{
    public async Task<PublishOutcome> PublishAsync(string payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");

        try
        {
            using var response = await client.PostAsync("/v1/ingest", content, cancellationToken);
            var outcome = PublishOutcomeClassifier.Classify(response.StatusCode);

            if (outcome == PublishOutcome.Rejected)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogError(
                    "Ingest refused a batch permanently with {Status}; dropping it. Body: {Body}",
                    (int)response.StatusCode, body);
            }
            else if (outcome == PublishOutcome.Retry)
            {
                logger.LogWarning(
                    "Ingest returned {Status}; keeping the batch for a later attempt",
                    (int)response.StatusCode);
            }

            return outcome;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
        {
            // Unreachable or timed out. This is the case the spool exists for.
            logger.LogWarning(ex, "Could not reach the ingest API; keeping the batch");
            return PublishOutcome.Retry;
        }
    }
}
