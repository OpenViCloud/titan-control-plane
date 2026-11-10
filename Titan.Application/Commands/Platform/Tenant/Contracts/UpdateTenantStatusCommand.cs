using Titan.Domain.Aggregates.Tenant;

namespace Titan.Application.Commands.Platform.Tenant.Contracts;

// Status as the endpoint read it once (UpdateTenantStatusCommandHandler.ParseTarget): an unknown word is 400 before the
// tenant is looked up (titan-ops features/tenant/design.md#tenant-status-repeats).
public record UpdateTenantStatusCommand(Guid TenantId, TenantStatus Status);
