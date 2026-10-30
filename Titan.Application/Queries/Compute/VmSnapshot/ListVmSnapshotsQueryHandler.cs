using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Application.Queries.Compute.VmSnapshot.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.VmSnapshot;

public class ListVmSnapshotsQueryHandler(
    IVmRepository vmRepo,
    IVmSnapshotRepository snapshotRepo,
    ICurrentUser currentUser)
{
    public async Task<List<VmSnapshotQueryResult>> HandleAsync(ListVmSnapshotsQuery query, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        await ComputeOwnership.RequireVmAsync(vmRepo, callerId, query.VmId, ct);

        var snapshots = await snapshotRepo.FindByVmIdAsync(query.VmId, ct);
        return snapshots.Select(VmSnapshotQueryResult.From).ToList();
    }
}
