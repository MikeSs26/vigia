using Microsoft.EntityFrameworkCore;
using Vigia.Core;
using Vigia.Core.Alerting;
using Vigia.Infrastructure;
using Vigia.Infrastructure.Entities;

namespace Vigia.Cli;

/// <summary>
/// Administrative operations. Deliberately not exposed over HTTP: there is no
/// registration endpoint to attack if registration is not an endpoint.
/// </summary>
public static class AdminCommands
{
    public static async Task<int> CreateTenantAsync(
        VigiaDbContext context, string name, string slug,
        DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var tenant = new Tenant { Name = name, Slug = slug, CreatedAt = createdAt };
        context.Tenants.Add(tenant);
        await context.SaveChangesAsync(cancellationToken);
        return tenant.Id;
    }

    public static async Task<int> CreateSourceAsync(
        VigiaDbContext context, int tenantId, string name,
        SourceKind kind, CancellationToken cancellationToken)
    {
        var source = new Source { TenantId = tenantId, Name = name, Kind = kind };
        context.Sources.Add(source);
        await context.SaveChangesAsync(cancellationToken);
        return source.Id;
    }

    /// <summary>Returns the plaintext key. It is not recoverable afterwards.</summary>
    public static async Task<string> IssueKeyAsync(
        VigiaDbContext context, int tenantId, string label,
        ApiKeyScope scope, DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        var (plainText, hash) = ApiKeyFactory.Create();

        context.ApiKeys.Add(new ApiKey
        {
            TenantId = tenantId,
            KeyHash = hash,
            Label = label,
            Scope = scope,
            CreatedAt = createdAt,
        });

        await context.SaveChangesAsync(cancellationToken);
        return plainText;
    }

    public static async Task<bool> RevokeKeyAsync(
        VigiaDbContext context, string keyHash,
        DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        var key = await context.ApiKeys
            .SingleOrDefaultAsync(k => k.KeyHash == keyHash, cancellationToken);

        if (key is null)
        {
            return false;
        }

        key.RevokedAt = revokedAt;
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static async Task<int> CreateChannelAsync(
        VigiaDbContext context, int tenantId, string name,
        Severity minSeverity, CancellationToken cancellationToken)
    {
        var channel = new NotificationChannelEntity
        {
            TenantId = tenantId,
            Kind = "discord_webhook",
            Name = name,
            MinSeverity = minSeverity,
            Enabled = true,
        };

        context.NotificationChannels.Add(channel);
        await context.SaveChangesAsync(cancellationToken);

        return channel.Id;
    }

    /// <summary>
    /// Creates a rule with no channel: recording and visible, delivering nothing.
    /// Opting a rule into a channel is a separate, deliberate act — see
    /// <see cref="AssignChannelAsync"/>.
    ///
    /// A null <paramref name="sourceId"/> targets every source of the tenant.
    /// </summary>
    public static async Task<int> CreateRuleAsync(
        VigiaDbContext context, int tenantId, string metricName,
        RuleAggregation aggregation, int windowSeconds, ComparisonOperator op,
        double threshold, int forSeconds, int noDataAfterSeconds,
        Severity severity, int cooldownSeconds, CancellationToken cancellationToken,
        int? sourceId = null)
    {
        var rule = new AlertRuleEntity
        {
            TenantId = tenantId,
            SourceId = sourceId,
            MetricName = metricName,
            Aggregation = aggregation,
            WindowSeconds = windowSeconds,
            Operator = op,
            Threshold = threshold,
            ForSeconds = forSeconds,
            NoDataAfterSeconds = noDataAfterSeconds,
            Severity = severity,
            ChannelId = null,
            CooldownSeconds = cooldownSeconds,
            Enabled = true,
        };

        context.AlertRules.Add(rule);
        await context.SaveChangesAsync(cancellationToken);

        return rule.Id;
    }

    /// <summary>
    /// Points a rule at a channel. Returns false when the rule does not exist, or
    /// when the channel does not belong to the rule's tenant.
    /// </summary>
    public static async Task<bool> AssignChannelAsync(
        VigiaDbContext context, int ruleId, int channelId, CancellationToken cancellationToken)
    {
        var rule = await context.AlertRules
            .SingleOrDefaultAsync(r => r.Id == ruleId, cancellationToken);

        if (rule is null)
        {
            return false;
        }

        // These tables carry no foreign keys, so the tenant match is enforced here
        // or nowhere — and a rule pointing at another tenant's channel would
        // deliver one tenant's incidents into another tenant's Discord.
        var channel = await context.NotificationChannels
            .SingleOrDefaultAsync(
                c => c.Id == channelId && c.TenantId == rule.TenantId, cancellationToken);

        if (channel is null)
        {
            return false;
        }

        rule.ChannelId = channelId;
        await context.SaveChangesAsync(cancellationToken);

        return true;
    }

    public static async Task<int> SilenceAsync(
        VigiaDbContext context, int tenantId, SilenceTarget target, int? targetId,
        int minutes, string reason, string createdBy, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var silence = new SilenceEntity
        {
            TenantId = tenantId,
            TargetKind = target,
            TargetId = targetId,
            Until = now.AddMinutes(minutes),
            Reason = reason,
            CreatedBy = createdBy,
        };

        context.Silences.Add(silence);
        await context.SaveChangesAsync(cancellationToken);

        return silence.Id;
    }

    public static async Task<int> UnsilenceAsync(
        VigiaDbContext context, int tenantId, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        await context.Silences
            .Where(s => s.TenantId == tenantId && s.Until > now)
            .ExecuteDeleteAsync(cancellationToken);
}
