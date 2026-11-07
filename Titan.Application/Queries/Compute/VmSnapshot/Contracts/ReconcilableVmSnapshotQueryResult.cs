using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using VmSnapshotStatus = Titan.Domain.Aggregates.Vm.VmSnapshotStatus;

namespace Titan.Application.Queries.Compute.VmSnapshot.Contracts;

// What the reconciler needs to find a snapshot on its VM and to report only what changed: CurrentExistence is whether
// Titan last knew Incus to hold it, worked out from the status and what a waiting operation last saw (the worker's
// contract kept its existence word, titan-ops features/vm/design.md#vm-snapshot-reports). VmId is for the callback
// route. Internal only — never on a customer path.
public record ReconcilableVmSnapshotQueryResult(Guid Id, Guid VmId, string ExternalId, string CurrentExistence)
{
    public static ReconcilableVmSnapshotQueryResult From(VmSnapshotEntity s) =>
        new(s.Id, s.VmId, s.GetExternalId(), ExistenceName(s));

    /// Exists when Incus was last seen holding the snapshot; NotFound otherwise, since every snapshot listed here has a
    /// provider id. A snapshot being deleted that Incus already lost stays NotFound, so the worker reports nothing new.
    public static string ExistenceName(VmSnapshotEntity s)
    {
        if (s.InstanceStatus == VmSnapshotStatus.Available)
        {
            return "Exists";
        }
        return "NotFound";
    }
}
