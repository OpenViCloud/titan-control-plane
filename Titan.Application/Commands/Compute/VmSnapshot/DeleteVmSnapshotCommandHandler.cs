using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using Titan.Domain.Repositories.Compute;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VmSnapshot;

public class DeleteVmSnapshotCommandHandler(
    IVmRepository vmRepo,
    IVmSnapshotRepository snapshotRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteVmSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var vmId = command.VmId;
        var vm = await ComputeOwnership.RequireVmAsync(vmRepo, callerId, vmId, ct);

        var snapshot = await ComputeOwnership.RequireVmSnapshotAsync(snapshotRepo, command.SnapshotId, vmId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This delete was already requested, or the snapshot is already deleted.
        // Answer like the first request: nothing recorded, nothing sent.
        bool alreadyRequestedOrDone = snapshot.Transition == VmSnapshotTransition.Deleting || snapshot.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        // The create failed before Incus had it: nothing to delete on Incus
        // (titan-ops architecture/desired-and-current.md#delete-locally). The request and its outcome happen here at once.
        bool neverReachedIncus = snapshot.ExternalId is null && snapshot.Transition == null;
        if (neverReachedIncus)
        {
            snapshot.DeleteLocally();
            activity.Stage("vm_snapshot.delete_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with DeleteLocally
            activity.StageOutcome(snapshot.TenantId, "vm_snapshot.deleted", "vm_snapshot", snapshot.Id, snapshot.Name);
            await snapshotRepo.SaveAsync(snapshot, ct);
            return;
        }

        string externalSnapshotId = snapshot.RequestDelete();
        activity.Stage("vm_snapshot.delete_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with RequestDelete

        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotDeleteAsync(snapshot.Id, vmId, externalSnapshotId, projectName, vm.ClusterId, ct);
        await snapshotRepo.SaveAsync(snapshot, ct);
    }
}
