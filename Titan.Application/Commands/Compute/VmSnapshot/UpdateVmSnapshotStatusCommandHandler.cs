using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using VmSnapshotTransition = Titan.Domain.Aggregates.Vm.VmSnapshotTransition;

namespace Titan.Application.Commands.Compute.VmSnapshot;

// Worker callback — an observation of Incus (titan-ops architecture/intent-and-observation.md#intent-versus-observation:
// observation converges). Authenticated by X-Internal-Token at the endpoint. Outcomes are recorded once: the aggregate
// says whether a report fulfilled the waiting request (titan-ops features/activity/design.md#outcome-source).
public class UpdateVmSnapshotStatusCommandHandler(IVmRepository vmRepo, IActivityRecorder activity)
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

        // The snapshot is reached through its VM (titan-ops architecture/code-structure.md#child-entity); a snapshot no VM
        // has, or one under another VM than the route names, is not found.
        VmAggregate? vm = await vmRepo.FindBySnapshotIdAsync(command.SnapshotId, ct);
        if (vm == null || vm.Id != command.VmId)
        {
            throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        }
        VmSnapshotEntity snapshot = vm.RequireSnapshot(command.SnapshotId);

        string? outcome = null;
        switch (req.Report)
        {
            case "Observed":
                // Seeing the snapshot never ends a revert: only the revert command's own report does.
                if (req.Exists == true)
                {
                    outcome = CreatedOutcome(vm.MarkSnapshotObserved(snapshot.Id));
                }
                else
                {
                    vm.MarkSnapshotNotFound(snapshot.Id);
                }
                break;
            case "Created":
                outcome = CreatedOutcome(vm.MarkSnapshotCreated(snapshot.Id, req.ExternalId!));
                break;
            case "Reverted":
                bool revertedNow = vm.MarkSnapshotReverted(snapshot.Id);
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
                    outcome = CreatedOutcome(vm.MarkSnapshotCreated(snapshot.Id, req.ExternalId!));
                }
                // A revert or delete Incus refused ends with the snapshot kept
                // (titan-ops architecture/desired-and-current.md#abandoned-request).
                bool revertOrDeleteWasWaiting = snapshot.Transition == VmSnapshotTransition.Reverting
                                                || snapshot.Transition == VmSnapshotTransition.Deleting;
                if (revertOrDeleteWasWaiting)
                {
                    vm.AbandonSnapshotRequest(snapshot.Id, req.Reason, req.Detail);
                }
                else
                {
                    vm.MarkSnapshotRequestFailed(snapshot.Id, req.Reason, req.Detail);
                }
                break;
            case "Deleted":
                bool deletedNow = vm.MarkSnapshotDeleted(snapshot.Id);
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

        await vmRepo.SaveAsync(ct);
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
