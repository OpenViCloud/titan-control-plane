using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;
using VolumeSnapshotEntity = Titan.Domain.Aggregates.Volume.VolumeSnapshot;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

// Worker callback — an observation of Incus (titan-ops architecture/intent-and-observation.md#intent-versus-observation:
// observation converges). Authenticated by X-Internal-Token at the endpoint. Outcomes are recorded once: the aggregate
// says whether a report fulfilled the waiting request (titan-ops features/activity/design.md#outcome-source).
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

        // The snapshot is reached through its volume (titan-ops architecture/code-structure.md#child-entity); a snapshot no
        // volume has, or one under another volume than the route names, is not found.
        VolumeAggregate? volume = await volumeRepo.FindBySnapshotIdAsync(command.SnapshotId, ct);
        if (volume == null || volume.Id != command.VolumeId)
        {
            throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        }
        VolumeSnapshotEntity snapshot = volume.RequireSnapshot(command.SnapshotId);

        string? outcome = null;
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == true)
                {
                    outcome = CreatedOutcome(volume.MarkSnapshotObserved(snapshot.Id));
                }
                else
                {
                    volume.MarkSnapshotNotFound(snapshot.Id);
                }
                break;
            case "Created":
                outcome = CreatedOutcome(volume.MarkSnapshotCreated(snapshot.Id, req.ExternalId!));
                break;
            case "RequestFailed":
                // A give-up carrying the id found the snapshot on Incus: the create happened.
                bool carriesAnIdNotYetStored = !string.IsNullOrEmpty(req.ExternalId) && snapshot.ExternalId == null;
                if (carriesAnIdNotYetStored)
                {
                    outcome = CreatedOutcome(volume.MarkSnapshotCreated(snapshot.Id, req.ExternalId!));
                }
                // A delete Incus refused gives the snapshot back (titan-ops architecture/desired-and-current.md#abandoned-request).
                bool deleteWasWaiting = snapshot.Transition == VolumeSnapshotTransition.Deleting;
                if (deleteWasWaiting)
                {
                    volume.AbandonSnapshotRequest(snapshot.Id, req.Reason, req.Detail);
                }
                else
                {
                    volume.MarkSnapshotRequestFailed(snapshot.Id, req.Reason, req.Detail);
                }
                break;
            case "Deleted":
                bool deletedNow = volume.MarkSnapshotDeleted(snapshot.Id);
                if (deletedNow)
                {
                    outcome = "volume_snapshot.deleted";
                }
                break;
        }

        if (outcome != null)
        {
            activity.StageOutcome(snapshot.TenantId, outcome, "volume_snapshot", snapshot.Id, snapshot.Name);
        }

        await volumeRepo.SaveAsync(ct);
    }

    private static string? CreatedOutcome(bool fulfilledTheCreate)
    {
        if (fulfilledTheCreate)
        {
            return "volume_snapshot.created";
        }
        return null;
    }
}
