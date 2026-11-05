using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Application.Queries.Admin.Vm.Contracts;

// Admin-facing — carries TenantId so no cross-tenant item is shown "orphaned" (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read).
// The status is a string so the UI contract is stable regardless of global enum-serializer config
// (titan-ops features/vm/design.md#vm-status).
public record AdminVmQueryResult(
    Guid Id,
    Guid TenantId,
    string Name,
    string Status,
    string? IpAddress,
    Guid NetworkId,
    string VmOfferingId,
    string TemplateId,
    string ClusterId,
    DateTimeOffset CreatedAt,
    string? RequestFailureReason,
    // Admin-only raw diagnostic — the tenant VM result deliberately omits this.
    string? RequestFailureDetail,
    DateTimeOffset? RequestFailedAt)
{
    public static AdminVmQueryResult From(VmAggregate v) =>
        new(v.Id, v.TenantId, v.Name, v.Status.ToString(), v.IpAddress, v.NetworkId, v.VmOfferingId, v.TemplateId, v.ClusterId, v.CreatedAt,
            v.OperationFailureReason, v.OperationFailureDetail, v.OperationFailedAt);

}
