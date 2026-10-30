using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Domain.Aggregates.VmSnapshot;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Application.Commands.Compute.VmSnapshot;

// Worker callback — an observation of Incus (titan-ops architecture/intent-and-observation.md#intent-versus-observation:
// observation converges). Authenticated by X-Internal-Token at the endpoint. Outcomes are recorded once: the aggregate
// says whether a report fulfilled the waiting request (titan-ops features/activity/design.md#outcome-source).
public class UpdateVmSnapshotStatusCommandHandler(IVmSnapshotRepository snapshotRepo, IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateVmSnapshotStatusCommand command, CancellationToken ct = default)
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
            case "Reverted":
            case "RequestFailed":
            case "Deleted":
                break;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown VM snapshot report: '{req.Report}'");
        }

        var snapshot = await snapshotRepo.FindByIdAsync(command.SnapshotId, ct)
            ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");

        if (snapshot.VmId != command.VmId)
            throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");

        string? outcome = null;
        switch (req.Report)
        {
            case "Observed":
                // Seeing the snapshot never ends a revert: only the revert command's own report does.
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
            case "Reverted":
                bool revertedNow = snapshot.MarkReverted();
                if (revertedNow)
                {
                    outcome = "vm_snapshot.reverted";
                }
                break;
            case "RequestFailed":
                // A give-up carrying the id found the snapshot on Incus: the create happened.
                bool carriesAnIdNotYetStored = !string.IsNullOrEmpty(req.ExternalId) && snapshot.ExternalId == null;
                if (carriesAnIdNotYetStored)
                {
                    outcome = CreatedOutcome(snapshot.MarkCreated(req.ExternalId!));
                }
                // A revert or delete Incus refused ends with the snapshot kept
                // (titan-ops architecture/desired-and-current.md#abandoned-request).
                bool revertOrDeleteWasWaiting = snapshot.Transition == VmSnapshotTransition.Reverting
                                                || snapshot.Transition == VmSnapshotTransition.Deleting;
                if (revertOrDeleteWasWaiting)
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
                    outcome = "vm_snapshot.deleted";
                }
                break;
        }

        if (outcome != null)
        {
            activity.StageOutcome(snapshot.TenantId, outcome, "vm_snapshot", snapshot.Id, snapshot.Name);
        }

        await snapshotRepo.SaveAsync(snapshot, ct);
    }

    private static string? CreatedOutcome(bool fulfilledTheCreate)
    {
        if (fulfilledTheCreate)
        {
            return "vm_snapshot.created";
        }
        return null;
    }
}
