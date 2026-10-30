using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using VmSnapshotTransition = Titan.Domain.Aggregates.Vm.VmSnapshotTransition;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VmSnapshot;

public class RevertVmSnapshotCommandHandler(
    IVmRepository vmRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(RevertVmSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var vmId = command.VmId;
        var vm = await ComputeOwnership.RequireVmAsync(vmRepo, callerId, vmId, ct);

        // A snapshot of another VM answers like a missing one (titan-ops architecture/code-structure.md#child-entity).
        VmSnapshotEntity snapshot = vm.RequireSnapshot(command.SnapshotId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This snapshot is already reverting.
        // Answer like the first request: nothing recorded, nothing sent.
        bool alreadyReverting = snapshot.Transition == VmSnapshotTransition.Reverting;
        if (alreadyReverting)
        {
            return;
        }

        // Checks the VM, then the snapshot, records the request, yields the id.
        string externalSnapshotId = vm.RevertSnapshot(snapshot.Id);
        activity.Stage("vm_snapshot.revert_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with Revert

        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotRevertAsync(snapshot.Id, vmId, externalSnapshotId, projectName, vm.ClusterId, ct);
        await vmRepo.SaveAsync(ct);
    }
}
