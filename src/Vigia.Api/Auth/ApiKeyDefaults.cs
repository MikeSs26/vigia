namespace Vigia.Api.Auth;

/// <summary>Names that only matter to ASP.NET Core's authentication plumbing.</summary>
public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string TenantClaim = "vigia:tenant";
    public const string ScopeClaim = "vigia:scope";
}
