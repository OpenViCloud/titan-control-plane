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
        OperationOutcome outcome = VmReports.ParseOutcome(command.Outcome);

        VmAggregate vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        VmSnapshotEntity snapshot = ComputeOwnership.RequireVmSnapshot(vm, command.SnapshotId);

        VmSnapshotOperationKind? ended;
        if (outcome == OperationOutcome.Done)
        {
            ended = vm.FinishSnapshotOperation(snapshot.Id, command.RequestId, command.ExternalId);
        }
        else
        {
            ended = vm.FailSnapshotOperation(snapshot.Id, command.RequestId, command.ExternalId, command.Reason,
                command.Detail);
        }
        if (ended == null)
        {
            return;
        }

        VmReports.RecordSnapshotEnded(snapshot, ended.Value, outcome, activity);
        await repo.SaveAsync(ct);
    }
}
