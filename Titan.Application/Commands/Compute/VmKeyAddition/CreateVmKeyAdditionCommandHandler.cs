using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmKeyAddition.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmKeyAdditionEntity = Titan.Domain.Aggregates.Vm.VmKeyAddition;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.VmKeyAddition;

public class CreateVmKeyAdditionCommandHandler(
    IVmRepository vmRepo,
    ISshKeyRepository sshKeyRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<CreateVmKeyAdditionCommandResult> HandleAsync(CreateVmKeyAdditionCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();

        // Locked: a second request on this VM while one waits takes its turn, as every other child's create does
        // (titan-ops architecture/code-structure.md#child-entity).
        VmAggregate vm = await ComputeOwnership.RequireVmForUpdateAsync(vmRepo, callerId, command.VmId, ct);

        // Every id must be a key of the caller's tenant, the same path VmVendorData uses for a VM's create
        // (titan-ops features/vm/design.md#ssh-key): a foreign id is indistinguishable from an unknown one.
        List<string> keyLines = await VmVendorData.KeyLinesAsync(sshKeyRepo, callerId, command.SshKeyIds, ct);

        Guid keyAdditionId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        string externalVmId = vm.AddKeys(keyAdditionId, operationId, command.SshKeyIds.Distinct().ToList());
        VmKeyAdditionEntity addition = ComputeOwnership.RequireVmKeyAddition(vm, keyAdditionId);
        activity.Stage("vm.add_keys_requested", "vm", vm.Id, vm.Name); // flushed atomically with the addition's insert; "vm.keys_added" is the outcome (titan-ops architecture/intent-and-observation.md#intent-has-identity)

        // Enqueued before the save, so the addition's row and its provider command commit in one transaction
        // (titan-ops architecture/provider-and-worker.md#provider-command-table).
        await commands.EnqueueVmKeyAdditionCreateAsync(vm, addition, operationId, externalVmId,
            TenantAggregate.ProjectNameFor(vm.TenantId), keyLines, ct);
        await vmRepo.SaveAsync(ct);

        return new CreateVmKeyAdditionCommandResult(keyAdditionId);
    }
}
