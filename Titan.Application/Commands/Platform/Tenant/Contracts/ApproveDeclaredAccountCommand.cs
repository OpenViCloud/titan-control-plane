namespace Titan.Application.Commands.Platform.Tenant.Contracts;

// Driven by TenantResolutionMiddleware once a PendingApproval caller's email is verified.
public record ApproveVerifiedSignupCommand(Guid TenantId);
