using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using Titan.Application.Exceptions;
using Titan.Application.Services;

namespace Titan.Application.Commands.Compute.VmSnapshot;

// The old callback route, alive until rollout phase 6 (titan-ops architecture/resource-lifecycle.md#callback-routes): each
// report of a worker that predates the request routes is mapped onto the request the snapshot is waiting on.
// Authenticated by X-Internal-Token at the endpoint, so no ownership check here. The snapshot is reached through the VM
// the route names, with the VM's row locked (titan-ops architecture/code-structure.md#child-entity).
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

        VmAggregate vm = await vmRepo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        VmSnapshotEntity snapshot = ComputeOwnership.RequireVmSnapshot(vm, command.SnapshotId);

        PendingOperation<VmSnapshotOperationKind>? waiting = snapshot.Pending;
        PendingOperation<VmSnapshotOperationKind>? ended = null;
        OperationOutcome outcome = OperationOutcome.Done;
        switch (req.Report)
        {
            case "Observed":
                // Seeing the snapshot ends nothing: only a command's own report does.
                if (req.Exists == true)
                {
                    vm.MarkSnapshotObserved(snapshot.Id);
                }
                else
                {
                    vm.MarkSnapshotNotFound(snapshot.Id);
                }
                break;
            case "Created":
                if (waiting is { Kind: VmSnapshotOperationKind.Create })
                {
                    ended = vm.FinishSnapshotOperation(snapshot.Id, waiting.Id, req.ExternalId);
                }
                break;
            case "Reverted":
                if (waiting is { Kind: VmSnapshotOperationKind.Revert })
                {
                    ended = vm.FinishSnapshotOperation(snapshot.Id, waiting.Id, null);
                }
                break;
            case "RequestFailed":
                // The waiting request ends Failed, keeping an id the create's give-up found.
                if (waiting != null)
                {
                    outcome = OperationOutcome.Failed;
                    ended = vm.FailSnapshotOperation(snapshot.Id, waiting.Id, req.ExternalId, req.Reason, req.Detail);
                }
                break;
            case "Deleted":
                // Only a delete's own report ends a snapshot; with no delete waiting it is gone from Incus, which is what
                // NotFound records.
                if (waiting is { Kind: VmSnapshotOperationKind.Delete })
                {
                    ended = vm.FinishSnapshotOperation(snapshot.Id, waiting.Id, null);
                }
                else
                {
                    vm.MarkSnapshotNotFound(snapshot.Id);
                }
                break;
        }

        if (ended != null)
        {
            VmReports.RecordSnapshotEnded(snapshot, ended, outcome, activity);
        }

        await vmRepo.SaveAsync(ct);
    }
}
