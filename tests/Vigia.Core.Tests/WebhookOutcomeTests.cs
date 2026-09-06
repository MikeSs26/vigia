using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public void TransientFailuresRetry(HttpStatusCode status)
    {
        Assert.Equal(PublishOutcome.Retry, WebhookOutcomeClassifier.Classify(status));
    }

    [Fact]
    public void RateLimitingIsItsOwnOutcomeRatherThanATransientFailure()
    {
        // 429 says "not now", not "this failed". Classifying it as Retry spends one
        // of the message's finite attempts on a refusal that carries no information
        // about the message, and draining a backlog rate-limits itself: the outbox
        // would destroy alerts during exactly the outage it exists to survive.
        Assert.Equal(
            PublishOutcome.RateLimited,
            WebhookOutcomeClassifier.Classify(HttpStatusCode.TooManyRequests));
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

    [Fact]
    public async Task ADiscordNoContentResponseIsDelivered()
    {
        // Pins the wiring from response to classifier, not the classifier alone.
        using var handler = new StubHandler(HttpStatusCode.NoContent);
        using var client = new HttpClient(handler);

        var publisher = new DiscordWebhookPublisher(client);

        Assert.Equal(
            PublishOutcome.Delivered,
            await publisher.PublishAsync("https://example.invalid/hook", "{}", default));
    }

    [Fact]
    public async Task ANetworkFailureRetriesInsteadOfPropagating()
    {
        // A timeout, a DNS failure or a torn connection is transient. What stops
        // this retrying forever is the outbox's attempt cap, not an exception here.
        using var handler = new ThrowingHandler(new HttpRequestException("connection reset"));
        using var client = new HttpClient(handler);

        var publisher = new DiscordWebhookPublisher(client);

        Assert.Equal(
            PublishOutcome.Retry,
            await publisher.PublishAsync("https://example.invalid/hook", "{}", default));
    }

    [Fact]
    public async Task ACancelledCallerTokenPropagatesRatherThanLookingLikeAWebhookFailure()
    {
        // Shutdown is not a Discord failure. Swallowing this into Retry would make
        // a stopping service look like a failing webhook, and would keep the drain
        // loop working while the host is trying to close.
        using var handler = new ThrowingHandler(new HttpRequestException("unreachable"));
        using var client = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var publisher = new DiscordWebhookPublisher(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => publisher.PublishAsync("https://example.invalid/hook", "{}", cts.Token));
    }
}

file sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status));
}

file sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw exception;
}
