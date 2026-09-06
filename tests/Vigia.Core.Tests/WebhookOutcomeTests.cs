using System.Net;
using Vigia.Infrastructure.Notifications;

namespace Vigia.Core.Tests;

public class WebhookOutcomeTests
{
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NoContent)]
    public void SuccessIsDelivered(HttpStatusCode status)
    {
        // Discord answers a webhook with 204 No Content, not 200.
        Assert.Equal(PublishOutcome.Delivered, WebhookOutcomeClassifier.Classify(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void TransientFailuresRetry(HttpStatusCode status)
    {
        Assert.Equal(PublishOutcome.Retry, WebhookOutcomeClassifier.Classify(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadRequest)]
    public void PermanentRefusalsAreNotRetried(HttpStatusCode status)
    {
        // A deleted webhook returns 404 forever. Retrying it burns the outbox
        // and hides the messages behind it.
        Assert.Equal(PublishOutcome.Rejected, WebhookOutcomeClassifier.Classify(status));
    }

    [Fact]
    public void AnUnrecognisedStatusRetries()
    {
        // Keeping the message costs a retry; discarding it costs the incident.
        Assert.Equal(PublishOutcome.Retry, WebhookOutcomeClassifier.Classify((HttpStatusCode)599));
    }
}
