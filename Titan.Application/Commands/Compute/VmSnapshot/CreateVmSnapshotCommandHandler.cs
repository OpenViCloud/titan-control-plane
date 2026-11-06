using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.Tenant;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
// Inside this namespace the bare name `VmSnapshot` binds to the namespace, not to the entity
// (CS0118, titan-ops architecture/code-structure.md#aggregate-alias). Same for `Vm`. The aliases say which one is meant.
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VmSnapshot;

public class CreateVmSnapshotCommandHandler(
    IVmRepository vmRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<CreateVmSnapshotCommandResult> HandleAsync(CreateVmSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        ComputeInput.EnsureVmSnapshotName(command.Name);
        var vmId = command.VmId;

        // Locked: the per-VM cap is counted on the snapshots loaded with the VM, so a second request at the cap waits
        // here and then counts this one (titan-ops architecture/code-structure.md#child-entity).
        VmAggregate vm = await ComputeOwnership.RequireVmForUpdateAsync(vmRepo, callerId, vmId, ct);

        // The cap is per VM and lives on the tenant (titan-ops architecture/tenancy-and-admin.md#tenant-quota), so it is
        // configurable per customer.
        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.VmSnapshot, vm.SnapshotQuotaUsage);

        // The VM checks itself (snapshottable) and adds the snapshot in one call.
        Guid snapshotId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        vm.AddSnapshot(snapshotId, operationId, command.Name);
        activity.Stage("vm_snapshot.create_requested", "vm_snapshot", snapshotId, command.Name); // atomic with the save
        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotCreateAsync(snapshotId, operationId, vmId, vm.ExternalId!, command.Name,
            projectName, vm.ClusterId, ct);
        await vmRepo.SaveAsync(ct);

        return new CreateVmSnapshotCommandResult(snapshotId);
    }
}
