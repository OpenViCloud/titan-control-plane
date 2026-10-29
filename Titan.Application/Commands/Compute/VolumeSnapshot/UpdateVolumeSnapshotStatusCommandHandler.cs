using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;

namespace Titan.Application.Commands.Compute.VolumeSnapshot;

// Worker callback — an observation of Incus (titan-ops architecture/intent-and-observation.md#intent-versus-observation:
// observation converges). Authenticated by X-Internal-Token at the endpoint. Outcomes are recorded once: the aggregate
// says whether a report fulfilled the waiting request (titan-ops features/activity/design.md#outcome-source).
public class UpdateVolumeSnapshotStatusCommandHandler(IVolumeSnapshotRepository snapshotRepo, IActivityRecorder activity)
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

        var snapshot = await snapshotRepo.FindByIdAsync(command.SnapshotId, ct)
            ?? throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");

        if (snapshot.VolumeId != command.VolumeId)
            throw new NotFoundException("VOLUME_SNAPSHOT_NOT_FOUND", "Snapshot not found.");

        string? outcome = null;
        switch (req.Report)
        {
            case "Observed":
                if (req.Exists == true)
                {
                    outcome = CreatedOutcome(snapshot.MarkObserved());
                }
                else
                {
                    snapshot.MarkNotFound();
                }
                break;
            case "Created":
                outcome = CreatedOutcome(snapshot.MarkCreated(req.ExternalId!));
                break;
            case "RequestFailed":
                // A give-up carrying the id found the snapshot on Incus: the create happened.
                bool carriesAnIdNotYetStored = !string.IsNullOrEmpty(req.ExternalId) && snapshot.ExternalId == null;
                if (carriesAnIdNotYetStored)
                {
                    outcome = CreatedOutcome(snapshot.MarkCreated(req.ExternalId!));
                }
                // A delete Incus refused gives the snapshot back (titan-ops architecture/desired-and-current.md#abandoned-request).
                bool deleteWasWaiting = snapshot.Transition == VolumeSnapshotTransition.Deleting;
                if (deleteWasWaiting)
                {
                    snapshot.AbandonRequest(req.Reason, req.Detail);
                }
                else
                {
                    snapshot.MarkRequestFailed(req.Reason, req.Detail);
                }
                break;
            case "Deleted":
                bool deletedNow = snapshot.MarkDeleted();
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

        await snapshotRepo.SaveAsync(snapshot, ct);
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
