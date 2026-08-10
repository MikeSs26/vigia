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

            default:
                stderr.WriteLine("""
                    Usage:
                      create-tenant <name> <slug>
                      create-source <tenantId> <name> <host|httpprobe>
                      issue-key     <tenantId> <label> <ingest|read|control>
                      revoke-key    <keyHash>
                    """);
                return 1;
        }
    }

    private static string ValidValues<TEnum>() where TEnum : struct, Enum =>
        string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()));
}
