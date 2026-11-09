using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Volume;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Compute.Vm;


public class DeleteVmCommandHandler(
    IVmRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IVolumeRepository volumeRepo,
    IActivityRecorder activity,
    VmDeletion deletion)
{
    public async Task HandleAsync(DeleteVmCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();

        // Locked, so a load balancer naming this VM as a backend waits for this request and then sees it
        // (titan-ops architecture/domain-services.md#cross-aggregate-lock).
        var vm = await ComputeOwnership.RequireVmForUpdateAsync(repo, callerId, command.VmId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). Already deleted, or a delete still
        // waiting: answer like the first request — nothing recorded, nothing sent
        // (titan-ops architecture/resource-lifecycle.md#repeat).
        if (vm.IsDeletedOrDeleting)
        {
            return;
        }

        // The VM refuses while a snapshot is being created or reverted (it is Snapshotting): the snapshot command would
        // act on an instance being deleted (titan-ops features/vm/design.md#vm-snapshot).
        // A load balancer still sending to it refuses too, rather than editing the load balancer behind the customer's
        // back (titan-ops features/load-balancer/design.md#lb-and-vm-network).
        Guid operationId = Guid.NewGuid();
        string? externalVmId = await deletion.DeleteAsync(vm, operationId, ct);

        activity.Stage("vm.delete_requested", "vm", vm.Id, vm.Name);

        // The volumes go with the VM's delete (titan-ops features/volume/design.md#vm-delete-releases-volumes), and it is
        // not a request of theirs (titan-ops architecture/resource-lifecycle.md#kinds): a volume with nothing waiting is no
        // longer asked to be attached; one with its own attach or detach waiting is left to that request.
        List<VolumeAggregate> attachedVolumes = await volumeRepo.ListAttachedToVmLockedAsync(vm.Id, ct);
        foreach (VolumeAggregate volume in attachedVolumes)
        {
            volume.ReleaseFromVmDelete(vm.Id);
        }

        // Never reached the provider (no instance name came back): no external resource, no reconciler running. The
        // request and its outcome happen here at once, so both rows go in the SaveChanges with the local delete.
        if (externalVmId == null)
        {
            activity.StageOutcome(vm.TenantId, "vm.deleted", "vm", vm.Id, vm.Name);
            await repo.SaveAsync(ct);
            return;
        }

        await commands.EnqueueVmDeleteAsync(vm, operationId, externalVmId, TenantAggregate.ProjectNameFor(vm.TenantId), ct);
        await repo.SaveAsync(ct); // request, request event and provider command together
    }
}
