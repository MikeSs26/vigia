namespace Vigia.Infrastructure.Entities;

public enum SourceKind
{
    Host = 0,
    HttpProbe = 1,
}

public sealed class Source
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public Tenant? Tenant { get; set; }

    public required string Name { get; set; }
    public SourceKind Kind { get; set; }
    public string Config { get; set; } = "{}";
    public DateTimeOffset? LastSeenAt { get; set; }
}
