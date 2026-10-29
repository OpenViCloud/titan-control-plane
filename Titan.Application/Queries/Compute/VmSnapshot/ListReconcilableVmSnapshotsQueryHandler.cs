using Titan.Application.Queries.Compute.VmSnapshot.Contracts;
using Titan.Domain.Repositories.Compute;

namespace Titan.Application.Queries.Compute.VmSnapshot;

// Read side of the VM snapshot reconciler (titan-ops architecture/desired-and-current.md#desired-and-current). No
// ICurrentUser: the reconciler acts for no tenant; the endpoint authenticates it by X-Internal-Token.
public class ListReconcilableVmSnapshotsQueryHandler(IVmSnapshotRepository snapshots)
{
    public async Task<List<ReconcilableVmSnapshotQueryResult>> HandleAsync(ListReconcilableVmSnapshotsQuery query, CancellationToken ct = default)
    {
        var rows = await snapshots.ListReconcilableAsync(query.ClusterId, ct);
        return rows.Select(ReconcilableVmSnapshotQueryResult.From).ToList();
    }
}
