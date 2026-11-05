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
        OperationOutcome outcome = VolumeReports.ParseOutcome(command.Outcome);

        VolumeAggregate volume = await repo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        VolumeSnapshotEntity snapshot = ComputeOwnership.RequireVolumeSnapshot(volume, command.SnapshotId);

        VolumeSnapshotOperationKind? ended;
        if (outcome == OperationOutcome.Done)
        {
            ended = volume.FinishSnapshotOperation(snapshot.Id, command.RequestId, command.ExternalId);
        }
        else
        {
            ended = volume.FailSnapshotOperation(snapshot.Id, command.RequestId, command.ExternalId, command.Reason,
                command.Detail);
        }
        if (ended == null)
        {
            return;
        }

        VolumeReports.RecordSnapshotEnded(snapshot, ended.Value, outcome, activity);
        await repo.SaveAsync(ct);
    }
}
