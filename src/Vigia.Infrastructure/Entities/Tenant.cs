namespace Vigia.Infrastructure.Entities;

public sealed class Tenant
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<ApiKey> ApiKeys { get; set; } = [];
    public ICollection<Source> Sources { get; set; } = [];
}
