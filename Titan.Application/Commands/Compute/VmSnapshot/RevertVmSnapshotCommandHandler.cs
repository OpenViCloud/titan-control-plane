using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.VmSnapshot;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VmSnapshot;

public class RevertVmSnapshotCommandHandler(
    IVmRepository vmRepo,
    IVmSnapshotRepository snapshotRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(RevertVmSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var vmId = command.VmId;
        var vm = await ComputeOwnership.RequireVmAsync(vmRepo, callerId, vmId, ct);

        var snapshot = await ComputeOwnership.RequireVmSnapshotAsync(snapshotRepo, command.SnapshotId, vmId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This snapshot is already reverting.
        // Answer like the first request: nothing recorded, nothing sent.
        bool alreadyReverting = snapshot.Transition == VmSnapshotTransition.Reverting;
        if (alreadyReverting)
        {
            return;
        }

        // Checks the VM and the snapshot, records the request, yields the id.
        string externalSnapshotId = snapshot.Revert(vm);
        activity.Stage("vm_snapshot.revert_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with Revert

        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotRevertAsync(snapshot.Id, vmId, externalSnapshotId, projectName, vm.ClusterId, ct);
        await snapshotRepo.SaveAsync(snapshot, ct);
    }
}
