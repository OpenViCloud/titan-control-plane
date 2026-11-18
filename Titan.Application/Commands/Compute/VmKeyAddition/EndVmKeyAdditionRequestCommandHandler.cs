using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmKeyAddition.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Application.Services;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmKeyAdditionEntity = Titan.Domain.Aggregates.Vm.VmKeyAddition;

namespace Titan.Application.Commands.Compute.VmKeyAddition;

// The worker's report that a key addition's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes).
// Authenticated by X-Internal-Token at the endpoint, acting for no tenant. The addition is reached through the VM
// the route names, with the VM's row locked, the same lock a request on it takes
// (titan-ops architecture/code-structure.md#child-entity); an addition that VM does not have is not found. A report
// the addition is not waiting on is ignored and still answers 204.
public class EndVmKeyAdditionRequestCommandHandler(IVmRepository repo, IActivityRecorder activity)
{
    public async Task HandleAsync(EndVmKeyAdditionRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the addition is loaded: a malformed report is the worker's bug, whatever it is.
        OperationOutcome outcome = OperationOutcomes.Parse(command.Outcome);

        VmAggregate vm = await repo.FindByIdForUpdateAsync(command.VmId, ct)
            ?? throw new NotFoundException("VM_KEY_ADDITION_NOT_FOUND", "Key addition not found.");
        VmKeyAdditionEntity addition = ComputeOwnership.RequireVmKeyAddition(vm, command.KeyAdditionId);

        bool ended = outcome == OperationOutcome.Done
            ? vm.KeyAdditionSucceeded(addition.Id, command.RequestId, command.RestartNeeded ?? false)
            : vm.KeyAdditionFailed(addition.Id, command.RequestId, command.Reason, command.Detail);
        if (!ended)
        {
            return;
        }

        // Create-shaped, but the operation acts on an existing VM rather than materialising a new resource, so a
        // give-up is as informative to the customer as a Stop or a Start giving up — it records an outcome, unlike
        // a VM/volume/network create's failure (titan-ops architecture/resource-lifecycle.md#kinds): a judgment
        // call, reported rather than measured against a written rule.
        string action = outcome == OperationOutcome.Done ? "vm.keys_added" : "vm.add_keys_failed";
        activity.StageOutcome(vm.TenantId, action, "vm", vm.Id, vm.Name);
        await repo.SaveAsync(ct);
    }
}
