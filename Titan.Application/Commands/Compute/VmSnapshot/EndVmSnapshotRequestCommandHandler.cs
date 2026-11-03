using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;

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
        RequestOutcome outcome = VmReports.ParseOutcome(command.Outcome);

        VmAggregate vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
        VmSnapshotEntity snapshot = ComputeOwnership.RequireVmSnapshot(vm, command.SnapshotId);

        // A create that ends Done must name the snapshot, or it would be left with nothing to revert or delete through.
        bool endsTheCreateWithoutAnId = outcome == RequestOutcome.Done
                                        && snapshot.Pending is { Kind: VmSnapshotRequestKind.Create } waiting
                                        && waiting.Id == command.RequestId
                                        && snapshot.ExternalId == null
                                        && string.IsNullOrEmpty(command.ExternalId);
        if (endsTheCreateWithoutAnId)
        {
            throw new BadRequestException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }

        VmSnapshotRequestEnd end = new(outcome, command.ExternalId, command.Reason, command.Detail);
        PendingRequest<VmSnapshotRequestKind>? ended = vm.EndSnapshotRequest(snapshot.Id, command.RequestId, end);
        if (ended == null)
        {
            return;
        }

        VmReports.RecordSnapshotEnded(snapshot, ended, outcome, activity);
        await repo.SaveAsync(ct);
    }
}
