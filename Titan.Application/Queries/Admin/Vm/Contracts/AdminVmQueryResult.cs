using Titan.Domain.Aggregates.Vm;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Application.Queries.Admin.Vm.Contracts;

// Admin-facing — carries TenantId so no cross-tenant item is shown "orphaned" (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-read).
// States are strings so the UI contract is stable regardless of global enum-serializer config
// (titan-ops architecture/desired-and-current.md#desired-and-current).
public record AdminVmQueryResult(
    Guid Id,
    Guid TenantId,
    string Name,
    string? CurrentState,
    string DesiredState,
    string CurrentExistence,
    string DesiredExistence,
    string? Transition,
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
        new(v.Id, v.TenantId, v.Name, StateName(v.CurrentState), v.DesiredState.ToString(),
            v.CurrentExistence.ToString(), v.DesiredExistence.ToString(), TransitionName(v.Transition),
            v.IpAddress, v.NetworkId, v.VmOfferingId, v.TemplateId, v.ClusterId, v.CreatedAt,
            v.RequestFailureReason, v.RequestFailureDetail, v.RequestFailedAt);

    private static string? StateName(VmState? state)
    {
        if (state == null)
        {
            return null;
        }
        return state.Value.ToString();
    }

    private static string? TransitionName(VmTransition? transition)
    {
        if (transition == null)
        {
            return null;
        }
        return transition.Value.ToString();
    }
}
