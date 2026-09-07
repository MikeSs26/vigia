namespace Vigia.Api.RateLimiting;

/// <summary>Names that only matter to ASP.NET Core's rate limiter plumbing.</summary>
public static class RateLimitingPolicies
{
    public const string Ingest = "ingest";

    public const string Read = "read";

    /// <summary>The public status page, which has no key to partition by.</summary>
    public const string Public = "public";
}
