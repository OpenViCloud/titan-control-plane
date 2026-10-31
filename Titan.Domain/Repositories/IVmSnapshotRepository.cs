using Titan.Domain.Aggregates.Vm;

namespace Titan.Domain.Repositories;

/// Read side of the VM snapshot table (titan-ops architecture/code-structure.md#child-entity): a snapshot is the VM's
/// child entity, written through IVmRepository. These lists are not bound to one aggregate.
public interface IVmSnapshotRepository
{
    /// The customer's list for one VM: every snapshot not Deleted.
    Task<List<VmSnapshot>> FindByVmIdAsync(Guid vmId, CancellationToken ct = default);
    /// All tenants: Incus holds it — a revert or a delete in flight still bills (titan-ops features/vm/design.md#vm-snapshot-counting).
    Task<List<VmSnapshot>> ListBillableAsync(CancellationToken ct = default);
    /// Snapshots of the cluster's VMs the VM snapshot reconciler observes: they have a provider id and are not Deleted.
    Task<List<ReconcilableVmSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
}
