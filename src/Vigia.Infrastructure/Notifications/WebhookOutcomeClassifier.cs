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

        // 429, 5xx and anything unrecognised: keeping the message costs a retry,
        // discarding it costs the incident.
        _ => PublishOutcome.Retry,
    };
}
