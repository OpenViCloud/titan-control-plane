using Titan.Domain.Aggregates.Compute.VmSnapshot;

namespace Titan.Domain.Repositories.Compute;

public interface IVmSnapshotRepository
{
    Task<VmSnapshot?> FindByIdAsync(Guid id, CancellationToken ct = default);
    Task<List<VmSnapshot>> FindByVmIdAsync(Guid vmId, CancellationToken ct = default);
    Task<int> CountActiveByVmIdAsync(Guid vmId, CancellationToken ct = default);
    Task<bool> AnyInProgressByVmIdAsync(Guid vmId, CancellationToken ct = default);
    /// All tenants: Incus holds it — a revert or a delete in flight still bills (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#vm-snapshot-counting).
    Task<List<VmSnapshot>> ListBillableAsync(CancellationToken ct = default);
    /// Snapshots of the cluster's VMs the VM snapshot reconciler observes: they have a provider id and are not Deleted.
    Task<List<VmSnapshot>> ListReconcilableAsync(string clusterId, CancellationToken ct = default);
    Task SaveAsync(VmSnapshot snapshot, CancellationToken ct = default);
    Task MarkAllDeletedByVmIdAsync(Guid vmId, CancellationToken ct = default);
}
