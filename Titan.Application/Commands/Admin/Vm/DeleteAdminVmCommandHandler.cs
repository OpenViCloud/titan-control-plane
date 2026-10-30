using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using Titan.Domain.Aggregates.Volume;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Admin.Vm;

// The only cross-tenant write on a tenant resource (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write):
// a platform admin deletes any tenant's VM. Bypasses tenant scope but not lifecycle rules — the same
// deletable-state and snapshot-in-progress guards as DeleteVmCommandHandler apply here.
//
// A separate admin handler, not a flag on the tenant one (titan-ops architecture/tenancy-and-admin.md#no-bypass-flag),
// so the tenant-facing Compute handlers never grow a bypass. The boundary is worth the small duplication.
public class DeleteAdminVmCommandHandler(
    IVmRepository vms,
    IVolumeRepository volumeRepo,
    IAdminAuditLogRepository auditLog,
    IProviderCommandQueue commands,
    ICurrentUser currentUser,
    ILogger<DeleteAdminVmCommandHandler> logger)
{
    public async Task HandleAsync(DeleteAdminVmCommand command, CancellationToken ct = default)
    {
        var vmId = command.VmId;

        var vm = await vms.FindByIdAsync(vmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        // The aggregate refuses a repeat, so it is recognised here first, whoever sent it
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). Already deleted: nothing to audit
        // (titan-ops features/vm/design.md#admin-delete-audit), nothing to send.
        if (vm.IsTerminal)
        {
            return;
        }

        // A delete still waiting: enqueue its command again, without a second audit row. The save is what
        // persists a reopened command row.
        bool deleteIsWaiting = vm.PendingIntent == VmIntent.Delete;
        if (deleteIsWaiting)
        {
            VmIntentTicket waiting = vm.ResendWaitingIntent(VmIntent.Delete);
            await commands.ResendVmDeleteAsync(vm.Id, waiting.ProviderId, TenantAggregate.ProjectNameFor(vm.TenantId),
                vm.ClusterId, ct);
            await vms.SaveAsync(ct);
            return;
        }

        // The VM refuses while one of its snapshots is being created or reverted (VM_SNAPSHOT_IN_PROGRESS), for an
        // admin as for the tenant (titan-ops architecture/code-structure.md#child-entity).
        VmIntentTicket ticket = vm.RequestDelete();

        // Audited with the destructive request (titan-ops architecture/tenancy-and-admin.md#admin-audit). Two sinks on
        // purpose: a structured log line for the log aggregator, and a durable append-only row that survives log loss.
        const string action = "vm.delete.cross_tenant";
        logger.LogWarning(
            "admin cross-tenant action {Action} by actor {Actor} on vm {VmId} owned by tenant {OwnerTenantId}",
            action, currentUser.KeycloakUserId, vm.Id, vm.TenantId);
        auditLog.Add(AdminAuditLog.Create(action, currentUser.KeycloakUserId, "vm", vm.Id, vm.TenantId));

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

        // Errored before provisioning — nothing at the provider, no reconciler running. The audit row
        // and DeleteLocally() persist together in one SaveChanges (same DbContext).
        if (ticket.Outcome == VmIntentOutcome.FinishLocally)
        {
            vm.DeleteLocally();
            await vms.SaveAsync(ct);
            return;
        }

        // The audit row, the pending intent and the provider command commit together (same DbContext), so
        // the worker never deletes a VM the audit log does not show
        // (titan-ops architecture/provider-and-worker.md#provider-command-table).
        // The project name derives from the owner's id, so no tenant lookup can fail between the audit row and its save.
        await commands.EnqueueVmDeleteAsync(vm.Id, ticket.ProviderId, TenantAggregate.ProjectNameFor(vm.TenantId),
            vm.ClusterId, ct);
        await vms.SaveAsync(ct);
    }
}
