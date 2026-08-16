using System.Security.Claims;
using Vigia.Api.Auth;
using Vigia.Api.RateLimiting;
using Vigia.Core;
using Vigia.Core.Querying;
using Vigia.Infrastructure.Querying;
using Vigia.Infrastructure.Series;

namespace Vigia.Api.Querying;

public static class SeriesEndpoint
{
    public static IEndpointRouteBuilder MapSeries(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/series", HandleAsync)
           .RequireAuthorization(ApiKeyScopes.Read)
           .RequireRateLimiting(RateLimitingPolicies.Read)
           .WithName("Series");

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string source,
        string name,
        DateTimeOffset from,
        DateTimeOffset to,
        string? granularity,
        string? agg,
        ClaimsPrincipal user,
        ISourceResolver sources,
        IMetricQueryReader reader,
        GranularityResolver resolver,
        CancellationToken cancellationToken)
    {
        if (!TryParseGranularity(granularity, out var requested))
        {
            return Results.Problem(
                $"Unknown granularity '{granularity}'. Use raw, 1m or 1h.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!TryParseAggregation(agg, out var aggregation))
        {
            return Results.Problem(
                $"Unknown aggregation '{agg}'. Use avg, min, max, last or count.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var resolution = resolver.Resolve(from, to, requested);
        if (!resolution.IsAllowed)
        {
            return Results.Problem(resolution.Refusal, statusCode: StatusCodes.Status400BadRequest);
        }

        // The tenant comes from the key, never from a parameter: that is what
        // makes tenancy structural rather than advisory.
        var tenantId = int.Parse(user.FindFirstValue(ApiKeyDefaults.TenantClaim)!);

        var sourceId = await sources.ResolveAsync(tenantId, source, cancellationToken);
        if (sourceId is null)
        {
            return Results.Problem(
                $"No source named '{source}'.", statusCode: StatusCodes.Status404NotFound);
        }

        var results = await reader.ReadAsync(
            new MetricQuery(
                tenantId, sourceId.Value, name,
                from.ToUniversalTime(), to.ToUniversalTime(),
                resolution.Granularity, aggregation),
            cancellationToken);

        return Results.Ok(new SeriesResponse(
            source,
            name,
            NameOf(resolution.Granularity),
            aggregation.ToString().ToLowerInvariant(),
            [.. results.Select(r => new SeriesPayload(
                r.Unit,
                r.Labels,
                [.. r.Points.Select(p => new PointPayload(p.Ts, p.Value))]))]));
    }

    private static bool TryParseGranularity(string? value, out Granularity? granularity)
    {
        switch (value?.ToLowerInvariant())
        {
            case null or "":
                granularity = null;
                return true;
            case "raw":
                granularity = Granularity.Raw;
                return true;
            case "1m":
                granularity = Granularity.OneMinute;
                return true;
            case "1h":
                granularity = Granularity.OneHour;
                return true;
            default:
                granularity = null;
                return false;
        }
    }

    private static bool TryParseAggregation(string? value, out Aggregation aggregation)
    {
        if (string.IsNullOrEmpty(value))
        {
            aggregation = Aggregation.Avg;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out aggregation);
    }

    private static string NameOf(Granularity granularity) => granularity switch
    {
        Granularity.Raw => "raw",
        Granularity.OneMinute => "1m",
        _ => "1h",
    };
}
