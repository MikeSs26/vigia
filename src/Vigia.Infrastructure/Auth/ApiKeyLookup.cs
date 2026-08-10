using Microsoft.EntityFrameworkCore;
using Vigia.Core;
using Vigia.Infrastructure.Entities;

namespace Vigia.Infrastructure.Auth;

public sealed class ApiKeyLookup(VigiaDbContext context) : IApiKeyLookup
{
    public async Task<ApiKeyRecord?> FindAsync(string keyHash, CancellationToken cancellationToken)
    {
        var key = await context.ApiKeys
            .AsNoTracking()
            .SingleOrDefaultAsync(
                k => k.KeyHash == keyHash && k.RevokedAt == null, cancellationToken);

        return key is null
            ? null
            : new ApiKeyRecord(key.Id, key.TenantId, ToScopeName(key.Scope));
    }

    private static string ToScopeName(ApiKeyScope scope) => scope switch
    {
        ApiKeyScope.Ingest => ApiKeyScopes.Ingest,
        ApiKeyScope.Read => ApiKeyScopes.Read,
        ApiKeyScope.Control => ApiKeyScopes.Control,
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };
}
