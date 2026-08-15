using System.Net;

namespace Vigia.Agent.Publishing;

public enum PublishOutcome
{
    /// <summary>The API took it. Discard the local copy.</summary>
    Accepted,

    /// <summary>Temporary. Keep the batch and try again later.</summary>
    Retry,

    /// <summary>The API will never accept this batch. Drop it and log why.</summary>
    Rejected,
}

public static class PublishOutcomeClassifier
{
    public static PublishOutcome Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Accepted => PublishOutcome.Accepted,

        HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.UnsupportedMediaType
            or HttpStatusCode.RequestEntityTooLarge => PublishOutcome.Rejected,

        // Everything else — 429, 5xx, and anything unrecognised — is treated as
        // temporary. Keeping data costs a retry; discarding it is permanent.
        _ => PublishOutcome.Retry,
    };
}

public interface IBatchPublisher
{
    Task<PublishOutcome> PublishAsync(string payload, CancellationToken cancellationToken);
}
