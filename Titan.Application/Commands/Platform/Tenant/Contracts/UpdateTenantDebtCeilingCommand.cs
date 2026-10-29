namespace Titan.Application.Commands.Platform.Tenant.Contracts;

// The API keeps decimal; the handler casts to long VND at the boundary (titan-ops architecture/money-and-wallet.md#money-is-long-vnd).
public record UpdateTenantDebtCeilingCommand(Guid TenantId, decimal DebtCeiling);
