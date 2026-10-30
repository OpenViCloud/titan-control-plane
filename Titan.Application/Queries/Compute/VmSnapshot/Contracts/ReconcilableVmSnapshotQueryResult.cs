using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;

namespace Titan.Application.Queries.Compute.VmSnapshot.Contracts;

// What the reconciler needs to find a snapshot on its VM and to report only what changed: CurrentExistence is what
// Titan last recorded. VmId is for the callback route. Internal only — never on a customer path.
public record ReconcilableVmSnapshotQueryResult(Guid Id, Guid VmId, string ExternalId, string CurrentExistence)
{
    public static ReconcilableVmSnapshotQueryResult From(VmSnapshotEntity s) =>
        new(s.Id, s.VmId, s.RequireExternalId(), s.CurrentExistence.ToString());
}
