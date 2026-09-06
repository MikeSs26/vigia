using Vigia.Core.Alerting;
using Vigia.Infrastructure;
using Vigia.Infrastructure.Entities;

namespace Vigia.Cli;

/// <summary>
/// Parses command-line arguments and dispatches to <see cref="AdminCommands"/>.
///
/// This is the only place argument validation happens. It writes through injected
/// <see cref="TextWriter"/>s rather than <see cref="Console"/> directly so tests can
/// drive it end to end - malformed input, stdout/stderr separation and exit codes
/// included - without spawning the built binary.
/// </summary>
public static class CliRunner
{
    public static async Task<int> RunAsync(
        string[] args, VigiaDbContext context, DateTimeOffset now,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        switch (args)
        {
            case ["create-tenant", var name, var slug]:
                var tenantId = await AdminCommands.CreateTenantAsync(context, name, slug, now, cancellationToken);
                stdout.WriteLine($"tenant {tenantId} created");
                return 0;

            case ["create-source", var tenant, var sourceName, var kind]:
                if (!int.TryParse(tenant, out var sourceTenantId))
                {
                    stderr.WriteLine($"Invalid tenant id '{tenant}': expected an integer.");
                    return 1;
                }

                if (!Enum.TryParse<SourceKind>(kind, ignoreCase: true, out var sourceKind))
                {
                    stderr.WriteLine($"Invalid source kind '{kind}': expected one of {ValidValues<SourceKind>()}.");
                    return 1;
                }

                var sourceId = await AdminCommands.CreateSourceAsync(
                    context, sourceTenantId, sourceName, sourceKind, cancellationToken);
                stdout.WriteLine($"source {sourceId} created");
                return 0;

            case ["issue-key", var tenant, var label, var scope]:
                if (!int.TryParse(tenant, out var keyTenantId))
                {
                    stderr.WriteLine($"Invalid tenant id '{tenant}': expected an integer.");
                    return 1;
                }

                if (!Enum.TryParse<ApiKeyScope>(scope, ignoreCase: true, out var keyScope))
                {
                    stderr.WriteLine($"Invalid scope '{scope}': expected one of {ValidValues<ApiKeyScope>()}.");
                    return 1;
                }

                var key = await AdminCommands.IssueKeyAsync(
                    context, keyTenantId, label, keyScope, now, cancellationToken);
                stdout.WriteLine(key);
                stderr.WriteLine("Store this now. It is not recoverable.");
                return 0;

            case ["revoke-key", var hash]:
                var revoked = await AdminCommands.RevokeKeyAsync(context, hash, now, cancellationToken);
                stdout.WriteLine(revoked ? "revoked" : "no such key");
                return revoked ? 0 : 1;

            case ["create-channel", var tenant, var channelName, var minSeverity]:
                if (!int.TryParse(tenant, out var channelTenantId))
                {
                    stderr.WriteLine($"Invalid tenant id '{tenant}': expected an integer.");
                    return 1;
                }

                if (!Enum.TryParse<Severity>(minSeverity, ignoreCase: true, out var parsedMinSeverity))
                {
                    stderr.WriteLine($"Invalid severity '{minSeverity}': expected one of {ValidValues<Severity>()}.");
                    return 1;
                }

                var channelId = await AdminCommands.CreateChannelAsync(
                    context, channelTenantId, channelName, parsedMinSeverity, cancellationToken);
                stdout.WriteLine($"channel {channelId} created");
                stderr.WriteLine($"Set the webhook URL in the environment as Notifier__ChannelWebhooks__{channelName}.");
                return 0;

            case ["create-rule", var tenant, var metric, var agg, var window, var op,
                  var threshold, var forSecs, var noData, var sev, var cooldown]:
            {
                if (!int.TryParse(tenant, out var ruleTenantId))
                {
                    stderr.WriteLine($"Invalid tenant id '{tenant}': expected an integer.");
                    return 1;
                }

                if (!Enum.TryParse<RuleAggregation>(agg, ignoreCase: true, out var parsedAgg))
                {
                    stderr.WriteLine($"Invalid aggregation '{agg}': expected one of {ValidValues<RuleAggregation>()}.");
                    return 1;
                }

                if (!Enum.TryParse<ComparisonOperator>(op, ignoreCase: true, out var parsedOp))
                {
                    stderr.WriteLine($"Invalid operator '{op}': expected one of {ValidValues<ComparisonOperator>()}.");
                    return 1;
                }

                if (!Enum.TryParse<Severity>(sev, ignoreCase: true, out var parsedSeverity))
                {
                    stderr.WriteLine($"Invalid severity '{sev}': expected one of {ValidValues<Severity>()}.");
                    return 1;
                }

                if (!int.TryParse(window, out var windowSeconds) || windowSeconds <= 0
                    || !int.TryParse(forSecs, out var forSeconds) || forSeconds < 0
                    || !int.TryParse(noData, out var noDataSeconds) || noDataSeconds <= 0
                    || !int.TryParse(cooldown, out var cooldownSeconds) || cooldownSeconds < 0
                    || !double.TryParse(threshold, out var parsedThreshold))
                {
                    stderr.WriteLine("Window, for, no-data and cooldown must be integers and threshold a number.");
                    return 1;
                }

                // Alerts read raw points, so a window must stay inside the raw retention
                // horizon. Refusing here is what keeps that guarantee.
                if (windowSeconds > 6 * 60 * 60)
                {
                    stderr.WriteLine("A rule window may span at most 6 hours.");
                    return 1;
                }

                var ruleId = await AdminCommands.CreateRuleAsync(
                    context, ruleTenantId, metric, parsedAgg, windowSeconds, parsedOp,
                    parsedThreshold, forSeconds, noDataSeconds, parsedSeverity,
                    cooldownSeconds, cancellationToken);

                stdout.WriteLine($"rule {ruleId} created");
                stderr.WriteLine("The rule is silent: it has no channel until one is assigned.");
                return 0;
            }

            case ["mute", var tenant, var minutes, var reason]:
                return await CreateSilenceAsync(
                    context, tenant, SilenceTarget.Global, null, minutes, reason, now,
                    stdout, stderr, cancellationToken);

            case ["silence", var tenant, var kind, var targetId, var minutes, var reason]:
            {
                if (!Enum.TryParse<SilenceTarget>(kind, ignoreCase: true, out var target)
                    || target == SilenceTarget.Global)
                {
                    stderr.WriteLine("Silence target must be 'rule' or 'source'; use 'mute' for global.");
                    return 1;
                }

                if (!int.TryParse(targetId, out var parsedTargetId))
                {
                    stderr.WriteLine($"Invalid target id '{targetId}': expected an integer.");
                    return 1;
                }

                return await CreateSilenceAsync(
                    context, tenant, target, parsedTargetId, minutes, reason, now,
                    stdout, stderr, cancellationToken);
            }

            case ["unsilence", var tenant]:
            {
                if (!int.TryParse(tenant, out var unsilenceTenantId))
                {
                    stderr.WriteLine($"Invalid tenant id '{tenant}': expected an integer.");
                    return 1;
                }

                var lifted = await AdminCommands.UnsilenceAsync(
                    context, unsilenceTenantId, now, cancellationToken);
                stdout.WriteLine($"{lifted} silence(s) lifted");
                return 0;
            }

            default:
                stderr.WriteLine("""
                    Usage:
                      create-tenant  <name> <slug>
                      create-source  <tenantId> <name> <host|httpprobe>
                      issue-key      <tenantId> <label> <ingest|read|control>
                      revoke-key     <keyHash>
                      create-channel <tenantId> <name> <info|warning|critical>
                      create-rule    <tenantId> <metric> <avg|min|max|last|p95|count> <windowSeconds>
                                     <gt|gte|lt|lte> <threshold> <forSeconds> <noDataAfterSeconds>
                                     <info|warning|critical> <cooldownSeconds>
                      mute           <tenantId> <minutes> <reason>
                      silence        <tenantId> <rule|source> <targetId> <minutes> <reason>
                      unsilence      <tenantId>
                    """);
                return 1;
        }
    }

    private static async Task<int> CreateSilenceAsync(
        VigiaDbContext context, string tenant, SilenceTarget target, int? targetId,
        string minutes, string reason, DateTimeOffset now,
        TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (!int.TryParse(tenant, out var tenantId))
        {
            stderr.WriteLine($"Invalid tenant id '{tenant}': expected an integer.");
            return 1;
        }

        // Expiry is mandatory. Nothing ends up permanently muted and forgotten.
        if (!int.TryParse(minutes, out var parsedMinutes) || parsedMinutes <= 0)
        {
            stderr.WriteLine("Duration in minutes must be a positive integer: every silence expires.");
            return 1;
        }

        var id = await AdminCommands.SilenceAsync(
            context, tenantId, target, targetId, parsedMinutes, reason, "cli", now, cancellationToken);

        stdout.WriteLine($"silence {id} created until {now.AddMinutes(parsedMinutes):o}");
        return 0;
    }

    private static string ValidValues<TEnum>() where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()));
}
