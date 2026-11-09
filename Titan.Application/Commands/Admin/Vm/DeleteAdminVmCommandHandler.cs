using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Admin.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using Titan.Domain.Aggregates.Volume;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Commands.Admin.Vm;

// The only cross-tenant write on a tenant resource (titan-ops architecture/tenancy-and-admin.md#admin-cross-tenant-write):
// a platform admin deletes any tenant's VM. Bypasses tenant scope but not lifecycle rules — the same
// status and load-balancer-backend guards as DeleteVmCommandHandler apply here (VmDeletion).
//
// A separate admin handler, not a flag on the tenant one (titan-ops architecture/tenancy-and-admin.md#no-bypass-flag),
// so the tenant-facing Compute handlers never grow a bypass. The boundary is worth the small duplication.
public class DeleteAdminVmCommandHandler(
    IVmRepository vms,
    IVolumeRepository volumeRepo,
    IAdminAuditLogRepository auditLog,
    IProviderCommandQueue commands,
    ICurrentUser currentUser,
    ILogger<DeleteAdminVmCommandHandler> logger,
    VmDeletion deletion)
{
    public async Task HandleAsync(DeleteAdminVmCommand command, CancellationToken ct = default)
    {
        var vmId = command.VmId;

        // Locked, as the tenant's delete, so a load balancer naming this VM waits and then sees it
        // (titan-ops architecture/domain-services.md#cross-aggregate-lock).
        var vm = await vms.FindByIdForUpdateAsync(vmId, ct)
            ?? throw new NotFoundException("VM_NOT_FOUND", "VM not found.");

        // The aggregate refuses a repeat, so it is recognised here first, whoever sent it
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). Already deleted, or a delete still
        // waiting: nothing to audit (titan-ops features/vm/design.md#admin-delete-audit), nothing to send
        // (titan-ops architecture/resource-lifecycle.md#repeat).
        if (vm.IsDeletedOrDeleting)
        {
            return;
        }

        // The VM refuses while a snapshot is being created or reverted (it is Snapshotting), or while a load balancer
        // still sends to it (VM_IS_LOAD_BALANCER_BACKEND), for an admin as for the tenant
        // (titan-ops features/vm/design.md#vm-snapshot · titan-ops architecture/domain-services.md#cross-aggregate-lock).
        Guid operationId = Guid.NewGuid();
        string? externalVmId = await deletion.DeleteAsync(vm, operationId, ct);

        // Audited with the destructive request (titan-ops architecture/tenancy-and-admin.md#admin-audit). Two sinks on
        // purpose: a structured log line for the log aggregator, and a durable append-only row that survives log loss.
        const string action = "vm.delete.cross_tenant";
        logger.LogWarning(
            "admin cross-tenant action {Action} by actor {Actor} on vm {VmId} owned by tenant {OwnerTenantId}",
            action, currentUser.KeycloakUserId, vm.Id, vm.TenantId);
        auditLog.Add(AdminAuditLog.Create(action, currentUser.KeycloakUserId, "vm", vm.Id, vm.TenantId));

        // The volumes go with the VM's delete (titan-ops features/volume/design.md#vm-delete-releases-volumes), and it is
        // not a request of theirs (titan-ops architecture/resource-lifecycle.md#kinds): a volume with nothing waiting is no
        // longer asked to be attached; one with its own attach or detach waiting is left to that request.
        List<VolumeAggregate> attachedVolumes = await volumeRepo.ListAttachedToVmLockedAsync(vm.Id, ct);
        foreach (VolumeAggregate volume in attachedVolumes)
        {
            volume.ReleaseFromVmDelete(vm.Id);
        }

        // Errored before provisioning (no instance name came back): nothing at the provider, no reconciler running.
        // The audit row and the local delete persist together in one SaveChanges (same DbContext).
        if (externalVmId == null)
        {
            await vms.SaveAsync(ct);
            return;
        }

        // The audit row, the waiting request and the provider command commit together (same DbContext), so
        // the worker never deletes a VM the audit log does not show
        // (titan-ops architecture/provider-and-worker.md#provider-command-table).
        // The project name derives from the owner's id, so no tenant lookup can fail between the audit row and its save.
        await commands.EnqueueVmDeleteAsync(vm, operationId, externalVmId, TenantAggregate.ProjectNameFor(vm.TenantId), ct);
        await vms.SaveAsync(ct);
    }
}
