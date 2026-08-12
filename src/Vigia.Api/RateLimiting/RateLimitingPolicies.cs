namespace Vigia.Api.RateLimiting;

/// <summary>Names that only matter to ASP.NET Core's rate limiter plumbing.</summary>
public static class RateLimitingPolicies
{
    public const string Ingest = "ingest";
}
