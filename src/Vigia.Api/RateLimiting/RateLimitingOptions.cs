namespace Vigia.Api.RateLimiting;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    /// <summary>Requests a single API key may make within <see cref="WindowSeconds"/>.</summary>
    public int PermitLimit { get; init; } = 120;

    /// <summary>Length of the fixed window, in seconds, that <see cref="PermitLimit"/> applies to.</summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>
    /// Requests queued once <see cref="PermitLimit"/> is exhausted, before further
    /// requests are rejected outright. Zero rejects immediately rather than holding
    /// requests open, matching the queue's own saturation behaviour.
    /// </summary>
    public int QueueLimit { get; init; }
}
