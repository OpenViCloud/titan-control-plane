namespace Titan.Application.Commands.Platform.Tenant.Contracts;

// Driven by TenantResolutionMiddleware on a caller's first authenticated request — not by an endpoint.
public record BootstrapTenantCommand(string KeycloakUserId, string Username);
