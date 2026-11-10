using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using Titan.Application.Services;

namespace Titan.Application.Commands.Compute.VmSnapshot;

// The worker's report that a VM snapshot request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant. The snapshot is reached through the VM the
// route names, with the VM's row locked, the same lock a request on the snapshot takes
// (titan-ops architecture/code-structure.md#child-entity); a snapshot that VM does not have is not found. A report the
// snapshot is not waiting on is ignored and still answers 204.
public class EndVmSnapshotRequestCommandHandler(IVmRepository repo, IActivityRecorder activity)
{
    public async Task HandleAsync(EndVmSnapshotRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the snapshot is loaded: a malformed report is the worker's bug, whatever the snapshot.
        OperationOutcome outcome = OperationOutcomes.Parse(command.Outcome);

        VmAggregate vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        VmSnapshotEntity snapshot = ComputeOwnership.RequireVmSnapshot(vm, command.SnapshotId);

        // The status the snapshot waits in says which operation the report ends.
        VmSnapshotStatus waiting = snapshot.Status;
        bool ended = EndTheOperation(vm, snapshot.Id, waiting, outcome, command);
        if (!ended)
        {
            return;
        }

        VmReports.RecordSnapshotEnded(snapshot, waiting, outcome, activity);
        await repo.SaveAsync(ct);
    }

    private static bool EndTheOperation(VmAggregate vm, Guid snapshotId, VmSnapshotStatus waiting,
        OperationOutcome outcome, EndVmSnapshotRequestCommand command)
    {
        bool done = outcome == OperationOutcome.Done;
        switch (waiting)
        {
            case VmSnapshotStatus.Creating:
                if (done)
                {
                    return vm.SnapshotCreateSucceeded(snapshotId, command.RequestId, command.ExternalId);
                }
                return vm.SnapshotCreateFailed(snapshotId, command.RequestId, command.ExternalId, command.Reason,
                    command.Detail);
            case VmSnapshotStatus.Reverting:
                if (done)
                {
                    return vm.SnapshotRevertSucceeded(snapshotId, command.RequestId);
                }
                return vm.SnapshotRevertFailed(snapshotId, command.RequestId, command.Reason, command.Detail);
            case VmSnapshotStatus.Deleting:
                if (done)
                {
                    return vm.SnapshotDeleteSucceeded(snapshotId, command.RequestId);
                }
                return vm.SnapshotDeleteFailed(snapshotId, command.RequestId, command.Reason, command.Detail);
            default:
                // Nothing waits: the report is stale.
                return false;
        }
    }
}
