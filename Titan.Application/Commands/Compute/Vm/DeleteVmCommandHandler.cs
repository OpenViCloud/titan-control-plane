using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Compute.Volume;
using VolumeAggregate = Titan.Domain.Aggregates.Compute.Volume.Volume;

namespace Titan.Application.Commands.Compute.Vm;


public class DeleteVmCommandHandler(
    IVmRepository repo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IVmSnapshotRepository snapshotRepo,
    IVolumeRepository volumeRepo,
    ILoadBalancerRepository loadBalancerRepo,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteVmCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        var vm = await ComputeOwnership.RequireVmAsync(repo, callerId, command.VmId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        // Already deleted: answer like the first request — nothing recorded, nothing sent.
        if (vm.IsTerminal)
        {
            return;
        }

        // A delete still waiting: enqueue it again under the same key. An open command absorbs it, a failed one
        // reopens (titan-ops architecture/provider-and-worker.md#provider-command-table); the save persists the reopen.
        bool deleteIsWaiting = vm.PendingIntent == VmIntent.Delete;
        if (deleteIsWaiting)
        {
            VmIntentTicket waiting = vm.ResendWaitingIntent(VmIntent.Delete);
            await commands.ResendVmDeleteAsync(vm.Id, waiting.ProviderId, tenant.ProjectName, vm.ClusterId, ct);
            await repo.SaveAsync(ct);
            return;
        }

        VmIntentTicket ticket = vm.RequestDelete();

        // A snapshot operation actively touching the VM (Creating/Reverting) races the delete on the
        // provider side — block until it settles (422). Nothing is saved yet, so the intent
        // RequestDelete recorded in memory is discarded with the request.
        if (await snapshotRepo.AnyInProgressByVmIdAsync(command.VmId, ct))
        {
            throw new BusinessException("VM_SNAPSHOT_IN_PROGRESS", "A snapshot operation is in progress for this VM.");
        }

        // Refused rather than editing the load balancer behind the customer's back
        // (titan-ops features/load-balancer/design.md#lb-and-vm-network). Same discard as above.
        if (await loadBalancerRepo.IsBackendAsync(command.VmId, ct))
        {
            throw new BusinessException("VM_IS_LOAD_BALANCER_BACKEND",
                "A load balancer still sends to this VM. Remove it from the load balancer first.");
        }

        activity.Stage("vm.delete_requested", "vm", vm.Id, vm.Name);

        // The volumes go with the VM's delete as detaches (titan-ops features/volume/design.md#vm-delete-releases-volumes);
        // one already being detached is left to its own request.
        List<VolumeAggregate> attachedVolumes = await volumeRepo.ListAttachedToVmAsync(vm.Id, ct);
        foreach (VolumeAggregate volume in attachedVolumes)
        {
            bool stillAskedToBeAttached = volume.DesiredAttachedVmId != null;
            if (stillAskedToBeAttached)
            {
                volume.DetachWithDeletedVm();
            }
        }

        // Never reached the provider — no external resource, no reconciler running. The request and
        // its outcome happen here at once, so both rows go in the SaveChanges with DeleteLocally().
        if (ticket.Outcome == VmIntentOutcome.FinishLocally)
        {
            vm.DeleteLocally();
            activity.StageOutcome(vm.TenantId, "vm.deleted", "vm", vm.Id, vm.Name);
            await repo.SaveAsync(ct);
            return;
        }

        await commands.EnqueueVmDeleteAsync(vm.Id, ticket.ProviderId, tenant.ProjectName, vm.ClusterId, ct);
        await repo.SaveAsync(ct); // intent, request event and provider command together
    }
}
