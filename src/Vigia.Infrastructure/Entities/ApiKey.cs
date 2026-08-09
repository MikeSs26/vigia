namespace Vigia.Infrastructure.Entities;

public enum ApiKeyScope
{
    Ingest = 0,
    Read = 1,
    Control = 2,
}

public sealed class ApiKey
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    /// <summary>SHA-256 of the plaintext key. The plaintext is never stored.</summary>
    public required string KeyHash { get; set; }

    public required string Label { get; set; }
    public ApiKeyScope Scope { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
