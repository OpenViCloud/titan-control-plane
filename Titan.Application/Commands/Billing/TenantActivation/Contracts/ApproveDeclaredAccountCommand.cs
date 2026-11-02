namespace Titan.Application.Commands.Billing.TenantActivation.Contracts;

// Driven by TenantResolutionMiddleware for a PendingApproval caller with the realm role auto-approve.
public record ApproveDeclaredAccountCommand(Guid TenantId);
