using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Titan.Domain.Aggregates.Platform.ApiToken;
using Titan.Domain.Repositories.Platform;

namespace Titan.Infrastructure.Auth;

/// Turns an API token into the principal of the member who created it: only `sub`, no email and no role. From there
/// TenantResolutionMiddleware finds the tenant through the member exactly as for a JWT, so suspension and pending
/// approval apply unchanged (titan-ops features/public-api/design.md#api-token).
public class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiTokenRepository tokens,
    ITenantRepository tenants,
    TimeProvider clock) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? secret = ApiTokenSchemes.ReadToken(Request.Headers.Authorization);
        if (secret == null)
        {
            return AuthenticateResult.NoResult();
        }

        if (!ApiToken.IsWellFormed(secret))
        {
            return AuthenticateResult.Fail("Malformed API token.");
        }

        ApiToken? token = await tokens.FindBySecretHashAsync(ApiToken.HashOf(secret), Context.RequestAborted);
        if (token == null)
        {
            return AuthenticateResult.Fail("Unknown API token.");
        }

        if (!token.IsUsableAt(clock.GetUtcNow()))
        {
            return AuthenticateResult.Fail("Expired API token.");
        }

        // A member who left (or whose tenant changed) must not keep acting through an old token. Without this a
        // `sub` with no member would reach BootstrapTenant, which needs an email claim the token does not carry.
        var membership = await tenants.FindTenantByMemberAsync(token.KeycloakUserId, Context.RequestAborted);
        if (membership == null || membership.Value.TenantId != token.TenantId)
        {
            return AuthenticateResult.Fail("The member who created this API token is no longer in its tenant.");
        }

        Claim[] claims = [new("sub", token.KeycloakUserId)];
        // The authentication type makes IsAuthenticated true; without it the middleware would skip the request.
        var identity = new ClaimsIdentity(claims, Scheme.Name, "sub", ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
