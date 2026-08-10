using Microsoft.EntityFrameworkCore;
using Vigia.Core;
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
}
