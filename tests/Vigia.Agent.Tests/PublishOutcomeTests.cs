using System.Net;
using Vigia.Agent.Publishing;

namespace Vigia.Agent.Tests;

public class PublishOutcomeTests
{
    [Fact]
    public void AcceptedIsTheOnlySuccess()
    {
        Assert.Equal(PublishOutcome.Accepted,
            PublishOutcomeClassifier.Classify(HttpStatusCode.Accepted));

        // The API answers 202, never 200. Treating 200 as success would hide a
        // contract change rather than surface it — so it must fall through to
        // Retry, not silently become Rejected.
        Assert.Equal(PublishOutcome.Retry,
            PublishOutcomeClassifier.Classify(HttpStatusCode.OK));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public void TransientConditionsAreRetried(HttpStatusCode status)
    {
        Assert.Equal(PublishOutcome.Retry, PublishOutcomeClassifier.Classify(status));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public void PermanentRefusalsAreRejectedRatherThanRetriedForever(HttpStatusCode status)
    {
        // Retrying these would park a batch the API will never accept at the head
        // of the spool, blocking every batch behind it.
        Assert.Equal(PublishOutcome.Rejected, PublishOutcomeClassifier.Classify(status));
    }

    [Fact]
    public void UnknownStatusesAreRetriedRatherThanDiscarded()
    {
        // When in doubt, keep the data. A retry costs a request; a wrong
        // "rejected" costs the measurement permanently.
        Assert.Equal(PublishOutcome.Retry, PublishOutcomeClassifier.Classify((HttpStatusCode)599));
    }
}
