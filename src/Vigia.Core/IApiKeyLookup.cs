namespace Vigia.Core;

public sealed record ApiKeyRecord(int Id, int TenantId, string Scope);

public interface IApiKeyLookup
{
    /// <summary>Returns the key, or null when it is unknown or revoked.</summary>
    Task<ApiKeyRecord?> FindAsync(string keyHash, CancellationToken cancellationToken);
}
