using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Vigia.Core;

namespace Vigia.Api.Auth;

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyLookup lookup)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var provided))
        {
            return AuthenticateResult.NoResult();
        }

        var plainText = provided.ToString();
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return AuthenticateResult.Fail("Empty API key.");
        }

        var record = await lookup.FindAsync(ApiKeyFactory.ComputeHash(plainText), Context.RequestAborted);
        if (record is null)
        {
            // Same message for unknown and revoked: distinguishing them tells an
            // attacker which of their guesses used to be valid.
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ApiKeyDefaults.TenantClaim, record.TenantId.ToString()),
            new Claim(ApiKeyDefaults.ScopeClaim, record.Scope),
            new Claim(ClaimTypes.NameIdentifier, record.Id.ToString()),
        ], ApiKeyDefaults.Scheme);

        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(
            new AuthenticationTicket(principal, ApiKeyDefaults.Scheme));
    }
}
