using System.Net;

namespace Vigia.Infrastructure.Notifications;

public static class WebhookOutcomeClassifier
{
    public static PublishOutcome Classify(HttpStatusCode status) => status switch
    {
        // Discord answers a webhook with 204; 200 is accepted for the ?wait=true form.
        HttpStatusCode.OK or HttpStatusCode.NoContent => PublishOutcome.Delivered,

        // A deleted or malformed webhook fails identically forever. Retrying it
        // burns the outbox and buries the messages behind it.
        HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.RequestEntityTooLarge => PublishOutcome.Rejected,

        // Not Retry. 429 says "not now", not "this message failed" — it carries no
        // information about the message at all. Spending an attempt on it means a
        // backlog drain, which necessarily outruns Discord's per-webhook limit,
        // rate-limits itself into the attempt cap and then destroys the alerts it
        // was recovering. The outbox size bound is what still stops unbounded growth.
        HttpStatusCode.TooManyRequests => PublishOutcome.RateLimited,

        // 5xx and anything unrecognised: keeping the message costs a retry,
        // discarding it costs the incident.
        _ => PublishOutcome.Retry,
    };
}
