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
    public DbSet<AlertRuleEntity> AlertRules => Set<AlertRuleEntity>();
    public DbSet<AlertInstance> AlertInstances => Set<AlertInstance>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<NotificationChannelEntity> NotificationChannels => Set<NotificationChannelEntity>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<SilenceEntity> Silences => Set<SilenceEntity>();

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

        builder.Entity<AlertRuleEntity>(entity =>
        {
            entity.ToTable("alert_rules");
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.TenantId).HasColumnName("tenant_id");
            entity.Property(r => r.SourceId).HasColumnName("source_id");
            entity.Property(r => r.MetricName).HasColumnName("metric_name").HasMaxLength(200);
            entity.Property(r => r.Aggregation).HasColumnName("aggregation");
            entity.Property(r => r.WindowSeconds).HasColumnName("window_seconds");
            entity.Property(r => r.Operator).HasColumnName("operator");
            entity.Property(r => r.Threshold).HasColumnName("threshold");
            entity.Property(r => r.ForSeconds).HasColumnName("for_seconds");
            entity.Property(r => r.NoDataAfterSeconds).HasColumnName("no_data_after_seconds");
            entity.Property(r => r.Severity).HasColumnName("severity");
            entity.Property(r => r.ChannelId).HasColumnName("channel_id");
            entity.Property(r => r.CooldownSeconds).HasColumnName("cooldown_seconds");
            entity.Property(r => r.Enabled).HasColumnName("enabled");
            entity.HasIndex(r => r.TenantId);
        });

        builder.Entity<AlertInstance>(entity =>
        {
            entity.ToTable("alert_instances");
            entity.Property(i => i.Id).HasColumnName("id");
            entity.Property(i => i.RuleId).HasColumnName("rule_id");
            entity.Property(i => i.SourceId).HasColumnName("source_id");
            entity.Property(i => i.State).HasColumnName("state");
            entity.Property(i => i.StateSince).HasColumnName("state_since");
            entity.Property(i => i.LastValue).HasColumnName("last_value");
            entity.Property(i => i.LastEvaluatedAt).HasColumnName("last_evaluated_at");
            entity.Property(i => i.FiredAt).HasColumnName("fired_at");
            entity.Property(i => i.LastNotifiedAt).HasColumnName("last_notified_at");
            entity.HasIndex(i => new { i.RuleId, i.SourceId }).IsUnique();
        });

        builder.Entity<AlertEvent>(entity =>
        {
            entity.ToTable("alert_events");
            entity.Property(e => e.Id).HasColumnName("id");
            entity.Property(e => e.InstanceId).HasColumnName("instance_id");
            entity.Property(e => e.FromState).HasColumnName("from_state");
            entity.Property(e => e.ToState).HasColumnName("to_state");
            entity.Property(e => e.At).HasColumnName("at");
            entity.Property(e => e.Value).HasColumnName("value");
            entity.Property(e => e.SuppressedReason).HasColumnName("suppressed_reason");
            entity.HasIndex(e => new { e.InstanceId, e.At });
        });

        builder.Entity<NotificationChannelEntity>(entity =>
        {
            entity.ToTable("notification_channels");
            entity.Property(c => c.Id).HasColumnName("id");
            entity.Property(c => c.TenantId).HasColumnName("tenant_id");
            entity.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(50);
            entity.Property(c => c.Name).HasColumnName("name").HasMaxLength(100);
            entity.Property(c => c.MinSeverity).HasColumnName("min_severity");
            entity.Property(c => c.Enabled).HasColumnName("enabled");
            entity.HasIndex(c => new { c.TenantId, c.Name }).IsUnique();
        });

        builder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("notification_outbox");
            entity.Property(m => m.Id).HasColumnName("id");
            entity.Property(m => m.ChannelId).HasColumnName("channel_id");
            entity.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            entity.Property(m => m.CreatedAt).HasColumnName("created_at");
            entity.Property(m => m.Attempts).HasColumnName("attempts");
            entity.Property(m => m.NextAttemptAt).HasColumnName("next_attempt_at");
            entity.Property(m => m.SentAt).HasColumnName("sent_at");
            entity.Property(m => m.LastError).HasColumnName("last_error");
            entity.Property(m => m.FailedAt).HasColumnName("failed_at");
            entity.HasIndex(m => m.NextAttemptAt);
        });

        builder.Entity<SilenceEntity>(entity =>
        {
            entity.ToTable("silences");
            entity.Property(s => s.Id).HasColumnName("id");
            entity.Property(s => s.TenantId).HasColumnName("tenant_id");
            entity.Property(s => s.TargetKind).HasColumnName("target_kind");
            entity.Property(s => s.TargetId).HasColumnName("target_id");
            entity.Property(s => s.Until).HasColumnName("until").IsRequired();
            entity.Property(s => s.Reason).HasColumnName("reason").HasMaxLength(500);
            entity.Property(s => s.CreatedBy).HasColumnName("created_by").HasMaxLength(100);
            entity.HasIndex(s => new { s.TenantId, s.Until });
        });
    }
}
