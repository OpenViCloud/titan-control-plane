using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
using VmSnapshotStatus = Titan.Domain.Aggregates.Vm.VmSnapshotStatus;
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
        // Locked, so two reverts of the snapshot take turns and the second sees the first waiting
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        var vm = await ComputeOwnership.RequireVmForUpdateAsync(vmRepo, callerId, vmId, ct);

        // A snapshot of another VM answers like a missing one (titan-ops architecture/code-structure.md#child-entity).
        VmSnapshotEntity snapshot = ComputeOwnership.RequireVmSnapshot(vm, command.SnapshotId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // This snapshot is already reverting.
        // Answer like the first request: nothing recorded, nothing sent.
        bool alreadyReverting = snapshot.Status == VmSnapshotStatus.Reverting;
        if (alreadyReverting)
        {
            return;
        }

        // Checks the VM, then the snapshot, and records the request under the id chosen here.
        Guid operationId = Guid.NewGuid();
        vm.RevertSnapshot(snapshot.Id, operationId);
        activity.Stage("vm_snapshot.revert_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with Revert

        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotRevertAsync(snapshot.Id, operationId, vmId, snapshot.GetExternalId(), projectName,
            vm.ClusterId, ct);
        await vmRepo.SaveAsync(ct);
    }
}
