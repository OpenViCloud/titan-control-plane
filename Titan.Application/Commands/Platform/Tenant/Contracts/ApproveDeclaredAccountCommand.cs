namespace Titan.Application.Commands.Platform.Tenant.Contracts;

// Driven by TenantResolutionMiddleware for a PendingApproval caller with the realm role auto-approve.
public record ApproveDeclaredAccountCommand(Guid TenantId);
