using Titan.Domain.Aggregates.Vm;

namespace Titan.Application.Queries.Compute.Vm.Contracts;

// What the reconciler needs to find a VM on the provider and to report only what changed: what Titan last
// recorded — the Incus status (null when none), whether the instance exists, and the addresses. Every
// observation that completes a waiting request changes one of them, so comparing never drops one. Internal
// only — never on a customer path.
public record ReconcilableVmQueryResult(
    Guid Id,
    string ExternalId,
    string? CurrentState,
    string CurrentExistence,
    string? IpAddress,
    string? PublicIpv6Address)
{
    public static ReconcilableVmQueryResult From(ReconcilableVm v) =>
        new(v.Id, v.ExternalId, StateName(v.CurrentState), v.CurrentExistence.ToString(), v.IpAddress,
            v.PublicIpv6Address);

    private static string? StateName(VmState? state)
    {
        if (state == null)
        {
            return null;
        }
        return state.Value.ToString();
    }
}
