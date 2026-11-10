using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

// The reconciler's observation of one volume snapshot (titan-ops architecture/resource-lifecycle.md#callback-routes):
// recorded without ending a request. Authenticated by X-Internal-Token at the endpoint; the volume's row is locked like
// every lifecycle handler's. A snapshot the volume does not have answers 404 from the volume itself (Volume.GetSnapshot).
public class ObserveVolumeSnapshotCommandHandler(IVolumeRepository repo)
{
    public async Task HandleAsync(ObserveVolumeSnapshotCommand command, CancellationToken ct = default)
    {
        VolumeAggregate volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        if (command.Exists)
        {
            volume.MarkSnapshotObserved(command.SnapshotId);
        }
        else
        {
            volume.MarkSnapshotNotFound(command.SnapshotId);
        }

        await repo.SaveAsync(ct);
    }
}
