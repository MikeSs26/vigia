using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Vigia.Api.Auth;

namespace Vigia.Api.RateLimiting;

/// <summary>
/// C2 (spec §10): without a per-API-key limit, a single key can drive the
/// ingest queue at will — the queue's own saturation shedding protects the
/// process, but a limit here means one noisy or misbehaving key can't crowd
/// out every other tenant sharing the same queue before saturation kicks in.
/// </summary>
public static class RateLimiterServiceCollectionExtensions
{
    public static IServiceCollection AddVigiaRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // 429, not the RateLimiter middleware's 503 default: a rate-limited
            // request must look like the queue's own saturation response, not a
            // different failure mode, so a client handles both the same way.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // A ceiling over EVERY request, including routes that carry no
            // policy of their own.
            //
            // This exists because authentication runs before the rate limiter,
            // and the API-key handler queries the database the moment an
            // X-Api-Key header is present — on any path, including /health and
            // paths that match nothing. Without a global limit, an anonymous
            // caller sending junk keys drives a PostgreSQL lookup per request
            // against the same ten-connection pool the ingestion, rollup and
            // alert workers share, and writes a log line per request to a
            // container log with no rotation. Measured at nearly three thousand
            // queries a second from one machine.
            //
            // Partitioned by address rather than by key, because the requests
            // this is defending against have no valid key. Generous enough that
            // no honest client meets it: a browser holding the status page open
            // refreshes twice a minute, and the agent posts once every ten
            // seconds.
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                httpContext => RateLimitPartition.GetFixedWindowLimiter(
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 300,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    }));

            options.OnRejected = async (context, cancellationToken) =>
            {
                var services = context.HttpContext.RequestServices;

                // Which window to advertise depends on which limit was hit. The
                // public page has its own, and quoting the API-key window there
                // would tell an anonymous caller to come back at the wrong time.
                var isPublic = context.HttpContext.Request.Path
                    .StartsWithSegments("/public", StringComparison.OrdinalIgnoreCase);

                var retryAfterSeconds = isPublic
                    ? services.GetRequiredService<IOptions<PublicStatus.PublicStatusOptions>>()
                        .Value.WindowSeconds
                    : services.GetRequiredService<IOptions<RateLimitingOptions>>()
                        .Value.WindowSeconds;

                context.HttpContext.Response.Headers[HeaderNames.RetryAfter] =
                    retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                // A keyed client benefits from knowing the budget is its own; an
                // anonymous one has no key to be told about.
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        error = isPublic
                            ? "Rate limit exceeded."
                            : "Rate limit exceeded for this API key.",
                    },
                    cancellationToken);
            };

            options.AddPolicy(RateLimitingPolicies.Ingest, httpContext =>
            {
                var rateLimiting = httpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitingOptions>>().Value;

                // Runs after authentication and authorization (UseRateLimiter is
                // registered after both in the pipeline), so User is always the
                // authenticated key's principal here — never the unauthenticated
                // fallback, since an unauthenticated or under-scoped request was
                // already turned away with 401/403 before reaching this point.
                var apiKeyId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(apiKeyId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimiting.PermitLimit,
                    Window = TimeSpan.FromSeconds(rateLimiting.WindowSeconds),
                    QueueLimit = rateLimiting.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });

            // Its own policy, not a shared one: AddPolicy builds a separate
            // partitioned limiter per policy, so a read key polling in a loop
            // exhausts only the read budget and cannot crowd out ingestion.
            options.AddPolicy(RateLimitingPolicies.Read, httpContext =>
            {
                var rateLimiting = httpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitingOptions>>().Value;

                var apiKeyId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(apiKeyId, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimiting.PermitLimit,
                    Window = TimeSpan.FromSeconds(rateLimiting.WindowSeconds),
                    QueueLimit = rateLimiting.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });

            // The public status page has no key to partition by, so it
            // partitions by client address instead.
            //
            // Behind a reverse proxy every request arrives from the proxy unless
            // forwarded headers are configured, which collapses this into a
            // single shared budget. That is a weaker guarantee than the per-key
            // policies give, and it is acceptable here only because the response
            // is served from a cache: the cost of a request that gets through is
            // rendering a string, not touching PostgreSQL.
            options.AddPolicy(RateLimitingPolicies.Public, httpContext =>
            {
                var publicStatus = httpContext.RequestServices
                    .GetRequiredService<IOptions<PublicStatus.PublicStatusOptions>>().Value;

                var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                return RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = publicStatus.PermitLimit,
                    Window = TimeSpan.FromSeconds(publicStatus.WindowSeconds),

                    // No queueing: an anonymous caller over the limit is turned
                    // away immediately rather than parked, holding a connection.
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                });
            });
        });

        return services;
    }
}
