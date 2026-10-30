using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
// Inside this namespace the bare name `VmSnapshot` binds to the namespace, not to the entity
// (CS0118, titan-ops architecture/code-structure.md#aggregate-alias). Same for `Vm`. The aliases say which one is meant.
using VmSnapshotEntity = Titan.Domain.Aggregates.Vm.VmSnapshot;
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
        var vmId = command.VmId;

        // Locked: the per-VM cap is counted on the snapshots loaded with the VM, so a second request at the cap waits
        // here and then counts this one (titan-ops architecture/code-structure.md#child-entity).
        VmAggregate vm = await ComputeOwnership.RequireVmForUpdateAsync(vmRepo, callerId, vmId, ct);

        // The cap is per VM and lives on the tenant (titan-ops architecture/tenancy-and-admin.md#tenant-quota), so it is
        // configurable per customer.
        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        // The VM checks itself (snapshottable), checks the cap and adds the snapshot in one call; the provider id is
        // read after, from a VM that just said it is snapshottable.
        VmSnapshotEntity snapshot = vm.AddSnapshot(tenant, command.Name);
        var externalVmId = vm.BeginSnapshot();
        activity.Stage("vm_snapshot.create_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with the save
        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotCreateAsync(snapshot.Id, vmId, externalVmId, command.Name, projectName,
            vm.ClusterId, ct);
        await vmRepo.SaveAsync(ct);

        return new CreateVmSnapshotCommandResult(snapshot.Id);
    }
}
