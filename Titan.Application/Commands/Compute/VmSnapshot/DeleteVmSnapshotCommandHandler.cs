using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using VmSnapshotTransition = Titan.Domain.Aggregates.Vm.VmSnapshotTransition;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VmSnapshot;

public class DeleteVmSnapshotCommandHandler(
    IVmRepository vmRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteVmSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var vmId = command.VmId;
        // Locked, so two deletes of the snapshot take turns and the second sees the first waiting
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        var vm = await ComputeOwnership.RequireVmForUpdateAsync(vmRepo, callerId, vmId, ct);

        // A snapshot of another VM answers like a missing one (titan-ops architecture/code-structure.md#child-entity).
        VmSnapshotEntity snapshot = ComputeOwnership.RequireVmSnapshot(vm, command.SnapshotId);

        // A repeat is answered like the first request: nothing recorded, nothing sent
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A delete that failed is not a
        // repeat: it falls through and the aggregate decides.
        if (snapshot.IsDeletedOrDeleting)
        {
            return;
        }

        // A snapshot that never reached Incus is deleted here and gets no ticket
        // (titan-ops architecture/desired-and-current.md#delete-locally): the request and its outcome at once.
        OperationTicket? ticket = vm.DeleteSnapshot(snapshot.Id);
        activity.Stage("vm_snapshot.delete_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with Delete
        if (ticket == null)
        {
            activity.StageOutcome(snapshot.TenantId, "vm_snapshot.deleted", "vm_snapshot", snapshot.Id, snapshot.Name);
            await vmRepo.SaveAsync(ct);
            return;
        }

        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotDeleteAsync(snapshot.Id, ticket.OperationId, vmId, ticket.ProviderId, projectName,
            vm.ClusterId, ct);
        await vmRepo.SaveAsync(ct);
    }
}
