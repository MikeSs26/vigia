using System.Globalization;
using System.Security.Claims;
using FluentValidation;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Vigia.Api.Auth;
using Vigia.Api.Queue;
using Vigia.Core;

namespace Vigia.Api.Ingest;

public static class IngestEndpoint
{
    public static IEndpointRouteBuilder MapIngest(this IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/ingest", HandleAsync)
           .RequireAuthorization(ApiKeyScopes.Ingest)
           .WithName("Ingest");

        return app;
    }

    private static async Task<IResult> HandleAsync(
        IngestRequest request,
        ClaimsPrincipal user,
        IValidator<IngestRequest> validator,
        IMetricQueue queue,
        IOptions<QueueOptions> queueOptions,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(validation.ToDictionary());
        }

        var tenantId = int.Parse(user.FindFirstValue(ApiKeyDefaults.TenantClaim)!);

        var points = new List<MetricPoint>(request.Points.Count);
        foreach (var point in request.Points)
        {
            // The validator already proved every name parses, so this branch is
            // unreachable in practice. The result is still checked rather than
            // discarded: MetricName is a struct, so a discarded failure leaves
            // `name` default-constructed with a null Value, and that null would
            // travel into the series identity before anything noticed. Checking
            // costs a branch per point and removes the only route by which an
            // unvalidated name can reach the database.
            if (!MetricName.TryCreate(point.Name, out var name))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    [nameof(IngestRequest.Points)] = [$"Invalid metric name '{point.Name}'."],
                });
            }

            points.Add(new MetricPoint(name, point.Unit, point.Ts, point.Value, point.Labels));
        }

        var batch = new MetricBatch(tenantId, request.Source, points);

        if (!await queue.TryEnqueueAsync(batch, cancellationToken))
        {
            // Saturated. Shedding here is the whole reason the queue is bounded:
            // a visible rejection beats an invisible slide into an OOM kill.
            // Retry-After turns that rejection into cooperative backpressure: a
            // client that honours it stops hammering an already-saturated queue.
            httpContext.Response.Headers[HeaderNames.RetryAfter] =
                queueOptions.Value.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);

            return Results.Json(
                new { error = "Ingestion queue is saturated." },
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        // 202, not 200: the batch is accepted, not yet persisted.
        return Results.Accepted(value: new { accepted = points.Count });
    }
}
