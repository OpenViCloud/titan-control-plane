using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Application.Queries.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Repositories;

namespace Titan.Application.Queries.Compute.VolumeSnapshot;

public class ListVolumeSnapshotsQueryHandler(
    IVolumeSnapshotRepository snapshotRepo,
    IVolumeRepository volumeRepo,
    ICurrentUser currentUser)
{
    public async Task<List<VolumeSnapshotQueryResult>> HandleAsync(ListVolumeSnapshotsQuery query, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        await ComputeOwnership.RequireVolumeAsync(volumeRepo, callerId, query.VolumeId, ct);

        var snapshots = await snapshotRepo.FindByVolumeIdAsync(query.VolumeId, ct);
        return snapshots.Select(VolumeSnapshotQueryResult.From).ToList();
    }
}
