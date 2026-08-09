using Microsoft.EntityFrameworkCore;
using Vigia.Infrastructure.Entities;

namespace Vigia.Infrastructure;

/// <summary>
/// Owns the transactional tables only. The time-series tables (metric_series,
/// metric_points) are created by raw SQL migrations and written through
/// <c>NpgsqlCopyMetricWriter</c>; EF Core never tracks them.
/// </summary>
public sealed class VigiaDbContext(DbContextOptions<VigiaDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Source> Sources => Set<Source>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Column names are spelled out in snake_case rather than left to EF's
        // PascalCase default. The time-series tables are created by raw SQL and
        // reference these columns; an unquoted identifier in SQL folds to
        // lowercase, so a column called "TenantId" would be unreachable as
        // tenant_id and the foreign keys would fail to create.
        builder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.Property(t => t.Id).HasColumnName("id");
            entity.Property(t => t.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(t => t.Slug).HasColumnName("slug").HasMaxLength(100);
            entity.Property(t => t.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(t => t.Slug).IsUnique();
        });

        builder.Entity<ApiKey>(entity =>
        {
            entity.ToTable("api_keys");
            entity.Property(k => k.Id).HasColumnName("id");
            entity.Property(k => k.TenantId).HasColumnName("tenant_id");
            entity.Property(k => k.KeyHash).HasColumnName("key_hash").HasMaxLength(64);
            entity.Property(k => k.Label).HasColumnName("label").HasMaxLength(200);
            entity.Property(k => k.Scope).HasColumnName("scope");
            entity.Property(k => k.CreatedAt).HasColumnName("created_at");
            entity.Property(k => k.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(k => k.RevokedAt).HasColumnName("revoked_at");
            entity.HasIndex(k => k.KeyHash).IsUnique();
            entity.HasOne(k => k.Tenant).WithMany(t => t.ApiKeys)
                  .HasForeignKey(k => k.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Source>(entity =>
        {
            entity.ToTable("sources");
            entity.Property(s => s.Id).HasColumnName("id");
            entity.Property(s => s.TenantId).HasColumnName("tenant_id");
            entity.Property(s => s.Name).HasColumnName("name").HasMaxLength(200);
            entity.Property(s => s.Kind).HasColumnName("kind");
            entity.Property(s => s.Config).HasColumnName("config").HasColumnType("jsonb");
            entity.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");
            entity.HasIndex(s => new { s.TenantId, s.Name }).IsUnique();
            entity.HasOne(s => s.Tenant).WithMany(t => t.Sources)
                  .HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
