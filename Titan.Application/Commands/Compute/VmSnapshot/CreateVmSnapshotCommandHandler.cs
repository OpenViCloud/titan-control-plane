using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmSnapshot.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Platform;
// Inside this namespace the bare name `VmSnapshot` binds to the namespace, not to the aggregate
// (CS0118, titan-ops architecture/code-structure.md#aggregate-alias). Same for `Vm`. The aliases say which one is meant.
using VmSnapshotAggregate = Titan.Domain.Aggregates.Compute.VmSnapshot.VmSnapshot;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VmSnapshot;

public class CreateVmSnapshotCommandHandler(
    IVmRepository vmRepo,
    IVmSnapshotRepository snapshotRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<CreateVmSnapshotCommandResult> HandleAsync(CreateVmSnapshotCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var vmId = command.VmId;
        var vm = await ComputeOwnership.RequireVmAsync(vmRepo, callerId, vmId, ct);

        // The cap is per VM and lives on the tenant (titan-ops architecture/tenancy-and-admin.md#tenant-quota), so it is
        // configurable per customer.
        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");
        var activeSnapshotCount = await snapshotRepo.CountActiveByVmIdAsync(vmId, ct);

        var snapshot = VmSnapshotAggregate.Create(vm, tenant, activeSnapshotCount, command.Name); // VM state AND quota
        var externalVmId = vm.BeginSnapshot();                     // same rule, and hands over the id
        activity.Stage("vm_snapshot.create_requested", "vm_snapshot", snapshot.Id, snapshot.Name); // atomic with the insert
        string projectName = TenantAggregate.ProjectNameFor(vm.TenantId);
        await commands.EnqueueVmSnapshotCreateAsync(snapshot.Id, vmId, externalVmId, command.Name, projectName,
            vm.ClusterId, ct);
        await snapshotRepo.SaveAsync(snapshot, ct);

        return new CreateVmSnapshotCommandResult(snapshot.Id);
    }
}
