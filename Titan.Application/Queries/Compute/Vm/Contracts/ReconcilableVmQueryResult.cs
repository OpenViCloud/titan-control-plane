using Titan.Domain.Aggregates.Vm;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Queries.Compute.Vm.Contracts;

// What the reconciler needs to find a VM on the provider and to report only what changed: what Titan last
// recorded — the Incus status (null when none), whether the instance exists, and the addresses. Internal only, never on
// a customer path. ProjectName is where the worker looks for the instance: an instance of the same name in another
// project is not this VM (titan-ops features/backup/design.md#incus-backup). The worker contract keeps its two fields (titan-ops features/vm/design.md#vm-status): CurrentState is
// the Incus status the VM holds (InstanceStatus), CurrentExistence is read off the status and that one.
public record ReconcilableVmQueryResult(
    Guid Id,
    string ExternalId,
    string? CurrentState,
    string CurrentExistence,
    string? IpAddress,
    string? PublicIpv6Address,
    string ProjectName)
{
    // The repository lists only VMs with a provider id, so a missing one here is a broken query, not a row to skip quietly.
    public static ReconcilableVmQueryResult From(VmAggregate v)
    {
        string externalId = v.ExternalId ?? throw new InvalidOperationException($"VM {v.Id} has no provider id and cannot be reconciled.");
        return new(v.Id, externalId, StateName(v.InstanceStatus), ExistenceName(v), v.IpAddress, v.PublicIpv6Address,
            TenantAggregate.ProjectNameFor(v.TenantId));
    }

    // The values of CurrentExistence the worker compares with: NotFound stops it repeating an absent instance, Exists
    // with a status stops it repeating a status Titan already has; anything else makes it report next cycle.
    private static string ExistenceName(VmAggregate v)
    {
        if (v.Status == VmStatus.NotFound)
        {
            return "NotFound";
        }
        if (v.InstanceStatus != null)
        {
            return "Exists";
        }
        return "NotCreated";
    }

    private static string? StateName(VmStatus? state)
    {
        if (state == null)
        {
            return null;
        }
        return state.Value.ToString();
    }
}
