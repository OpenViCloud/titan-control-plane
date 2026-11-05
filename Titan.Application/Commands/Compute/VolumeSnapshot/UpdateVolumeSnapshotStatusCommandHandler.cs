using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;
using Titan.Application.Exceptions;
using Titan.Application.Services;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): each
// report of a worker that predates the request routes is mapped onto the request the snapshot is waiting on.
// Authenticated by X-Internal-Token at the endpoint.
public class UpdateVolumeSnapshotStatusCommandHandler(IVolumeRepository volumeRepo, IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateVolumeSnapshotStatusCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the callback payload (titan-ops architecture/code-structure.md#contracts)

        // Parsed before the snapshot is loaded: a malformed report is the worker's bug, whatever the snapshot.
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == null)
                {
                    throw new BadRequestException("INVALID_STATUS", "Exists is required when report is Observed.");
                }
                break;
            case "Created":
                if (string.IsNullOrEmpty(req.ExternalId))
                {
                    throw new BadRequestException("INVALID_STATUS", "ExternalId is required when report is Created.");
                }
                break;
            case "RequestFailed":
            case "Deleted":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown volume snapshot report: '{req.Report}'");
        }

        // The snapshot is reached through its volume (titan-ops architecture/code-structure.md#child-entity); a snapshot the
        // named volume does not have is not found.
        VolumeAggregate volume = await volumeRepo.FindByIdForUpdateAsync(command.VolumeId, ct)
            ?? throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        VolumeSnapshotEntity snapshot = ComputeOwnership.RequireVolumeSnapshot(volume, command.SnapshotId);

        PendingOperation<VolumeSnapshotOperationKind>? waiting = snapshot.Pending;
        PendingOperation<VolumeSnapshotOperationKind>? ended = null;
        OperationOutcome outcome = OperationOutcome.Done;
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == true)
                {
                    volume.MarkSnapshotObserved(snapshot.Id);
                }
                else
                {
                    volume.MarkSnapshotNotFound(snapshot.Id);
                }
                break;
            case "Created":
                if (waiting is { Kind: VolumeSnapshotOperationKind.Create })
                {
                    ended = volume.FinishSnapshotOperation(snapshot.Id, waiting.Id, req.ExternalId);
                }
                break;
            case "RequestFailed":
                if (waiting != null)
                {
                    outcome = OperationOutcome.Failed;
                    ended = volume.FailSnapshotOperation(snapshot.Id, waiting.Id, req.ExternalId, req.Reason, req.Detail);
                }
                break;
            case "Deleted":
                if (waiting is { Kind: VolumeSnapshotOperationKind.Delete })
                {
                    ended = volume.FinishSnapshotOperation(snapshot.Id, waiting.Id, null);
                }
                else
                {
                    volume.MarkSnapshotNotFound(snapshot.Id);
                }
                break;
        }

        if (ended != null)
        {
            VolumeReports.RecordSnapshotEnded(snapshot, ended, outcome, activity);
        }

        await volumeRepo.SaveAsync(ct);
    }
}
