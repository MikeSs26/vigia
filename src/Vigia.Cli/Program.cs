using Microsoft.EntityFrameworkCore;
using Vigia.Cli;
using Vigia.Infrastructure;
using Vigia.Infrastructure.Entities;

var connectionString = Environment.GetEnvironmentVariable("VIGIA_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("Set VIGIA_CONNECTION to the PostgreSQL connection string.");
    return 1;
}

var options = new DbContextOptionsBuilder<VigiaDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var context = new VigiaDbContext(options);
var now = TimeProvider.System.GetUtcNow();

switch (args)
{
    case ["create-tenant", var name, var slug]:
        var tenantId = await AdminCommands.CreateTenantAsync(context, name, slug, now, default);
        Console.WriteLine($"tenant {tenantId} created");
        return 0;

    case ["create-source", var tenant, var sourceName, var kind]:
        var sourceId = await AdminCommands.CreateSourceAsync(
            context, int.Parse(tenant), sourceName, Enum.Parse<SourceKind>(kind, true), default);
        Console.WriteLine($"source {sourceId} created");
        return 0;

    case ["issue-key", var tenant, var label, var scope]:
        var key = await AdminCommands.IssueKeyAsync(
            context, int.Parse(tenant), label, Enum.Parse<ApiKeyScope>(scope, true), now, default);
        Console.WriteLine(key);
        Console.Error.WriteLine("Store this now. It is not recoverable.");
        return 0;

    case ["revoke-key", var hash]:
        var revoked = await AdminCommands.RevokeKeyAsync(context, hash, now, default);
        Console.WriteLine(revoked ? "revoked" : "no such key");
        return revoked ? 0 : 1;

    default:
        Console.Error.WriteLine("""
            Usage:
              create-tenant <name> <slug>
              create-source <tenantId> <name> <host|httpprobe>
              issue-key     <tenantId> <label> <ingest|read|control>
              revoke-key    <keyHash>
            """);
        return 1;
}
