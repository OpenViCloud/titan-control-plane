namespace Titan.Application.Commands.Billing.TenantActivation.Contracts;

// Driven by UpdateTenantStatusEndpoint when the platform admin sets a tenant Active.
public record ActivateTenantCommand(Guid TenantId);
