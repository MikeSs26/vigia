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

            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfterSeconds = context.HttpContext.RequestServices
                    .GetRequiredService<IOptions<RateLimitingOptions>>().Value.WindowSeconds;

                context.HttpContext.Response.Headers[HeaderNames.RetryAfter] =
                    retryAfterSeconds.ToString(CultureInfo.InvariantCulture);

                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Rate limit exceeded for this API key." },
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
        });

        return services;
    }
}
