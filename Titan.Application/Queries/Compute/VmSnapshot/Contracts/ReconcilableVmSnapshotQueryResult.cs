using Titan.Domain.Aggregates.Vm;

namespace Titan.Application.Queries.Compute.VmSnapshot.Contracts;

// What the reconciler needs to find a snapshot on its VM and to report only what changed: CurrentExistence is what
// Titan last recorded. VmId is for the callback route. Internal only — never on a customer path.
public record ReconcilableVmSnapshotQueryResult(Guid Id, Guid VmId, string ExternalId, string CurrentExistence)
{
    public static ReconcilableVmSnapshotQueryResult From(ReconcilableVmSnapshot s) =>
        new(s.Id, s.VmId, s.ExternalId, s.CurrentExistence.ToString());
}
