using Titan.Domain.Aggregates.Vm;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

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
    // The repository lists only VMs with a provider id, so a missing one here is a broken query, not a row to skip quietly.
    public static ReconcilableVmQueryResult From(VmAggregate v)
    {
        string externalId = v.ExternalId ?? throw new InvalidOperationException($"VM {v.Id} has no provider id and cannot be reconciled.");
        return new(v.Id, externalId, StateName(v.CurrentState), v.CurrentExistence.ToString(), v.IpAddress, v.PublicIpv6Address);
    }

    private static string? StateName(VmState? state)
    {
        if (state == null)
        {
            return null;
        }
        return state.Value.ToString();
    }
}
