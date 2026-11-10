using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;
using Titan.Application.Services;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

// The worker's report that a volume snapshot request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant. The snapshot is reached through its volume,
// whose row is locked (titan-ops architecture/code-structure.md#child-entity); a snapshot the named volume does not have is
// not found. A report the snapshot is not waiting on is ignored and still answers 204.
public class EndVolumeSnapshotRequestCommandHandler(IVolumeRepository repo, IActivityRecorder activity)
{
    public async Task HandleAsync(EndVolumeSnapshotRequestCommand command, CancellationToken ct = default)
    {
        OperationOutcome outcome = OperationOutcomes.Parse(command.Outcome);

        VolumeAggregate volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        VolumeSnapshotEntity snapshot = ComputeOwnership.RequireVolumeSnapshot(volume, command.SnapshotId);

        // The status the snapshot waits in says which operation the report ends.
        VolumeSnapshotStatus waiting = snapshot.Status;
        bool ended = EndTheOperation(volume, snapshot.Id, waiting, outcome, command);
        if (!ended)
        {
            return;
        }

        VolumeReports.RecordSnapshotEnded(snapshot, waiting, outcome, activity);
        await repo.SaveAsync(ct);
    }

    private static bool EndTheOperation(VolumeAggregate volume, Guid snapshotId, VolumeSnapshotStatus waiting,
        OperationOutcome outcome, EndVolumeSnapshotRequestCommand command)
    {
        bool done = outcome == OperationOutcome.Done;
        switch (waiting)
        {
            case VolumeSnapshotStatus.Creating:
                if (done)
                {
                    return volume.SnapshotCreateSucceeded(snapshotId, command.RequestId, command.ExternalId);
                }
                return volume.SnapshotCreateFailed(snapshotId, command.RequestId, command.ExternalId, command.Reason,
                    command.Detail);
            case VolumeSnapshotStatus.Deleting:
                if (done)
                {
                    return volume.SnapshotDeleteSucceeded(snapshotId, command.RequestId);
                }
                return volume.SnapshotDeleteFailed(snapshotId, command.RequestId, command.Reason, command.Detail);
            default:
                // Nothing waits: the report is stale.
                return false;
        }
    }
}
