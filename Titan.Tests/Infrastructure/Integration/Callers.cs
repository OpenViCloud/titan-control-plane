using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Titan.Application.Abstractions;
using Titan.Infrastructure.Auth;

namespace Titan.Tests.Infrastructure.Integration;

// The production CurrentUser over a request carrying the caller, as TenantResolutionMiddleware leaves it.
// titan-ops architecture/testing.md#doubles-only-in-unit-tests
internal static class Callers
{
    public static ICurrentUser Admin(string keycloakUserId)
    {
        var request = new DefaultHttpContext { User = Signed(keycloakUserId, "platform-admin") };
        return new CurrentUser(new HttpContextAccessor { HttpContext = request });
    }

    public static ICurrentUser TenantMember(Guid tenantId)
    {
        var request = new DefaultHttpContext { User = Signed("member-" + tenantId.ToString("N"), role: null) };
        request.Items["TenantId"] = tenantId;
        return new CurrentUser(new HttpContextAccessor { HttpContext = request });
    }

    private static ClaimsPrincipal Signed(string keycloakUserId, string? role)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, keycloakUserId) };
        if (role != null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }
}
