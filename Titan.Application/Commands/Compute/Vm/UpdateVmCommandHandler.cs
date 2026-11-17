using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Application.Commands.Compute.Vm;


// A command that enqueues nothing: the name and the stop timeout are Titan-side only, the provider never hears about
// them (the next stop's command carries the timeout). It still belongs in Commands/ because it WRITES — the split is
// read-vs-write, not remote-vs-local. One handler for both fields, so a request carrying both is applied in one save or
// refused whole.
public class UpdateVmCommandHandler(IVmRepository repo, ICurrentUser currentUser)
{
    // Returns nothing on purpose (titan-ops architecture/code-structure.md#no-contract-reuse): a caller that wants the
    // changed VM asks the query side for it.
    public async Task HandleAsync(UpdateVmCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        if (command.Name != null)
        {
            ComputeInput.EnsureVmName(command.Name);
        }
        if (command.StopTimeoutSeconds != null)
        {
            ComputeInput.EnsureStopTimeout(command.StopTimeoutSeconds.Value);
        }
        Domain.Aggregates.Vm.Vm vm = await ComputeOwnership.RequireVmAsync(repo, tenantId, command.VmId, ct);

        // A value asked for that is already the value is done, nothing to write
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        bool renames = command.Name != null && command.Name != vm.Name;
        bool changesStopTimeout = command.StopTimeoutSeconds != null && command.StopTimeoutSeconds != vm.StopTimeoutSeconds;
        if (!renames && !changesStopTimeout)
        {
            return;
        }

        if (renames)
        {
            bool nameTaken = await repo.ExistsByNameAsync(tenantId, command.Name!, ct);
            if (nameTaken)
            {
                throw new ConflictException("VM_NAME_TAKEN", $"VM name '{command.Name}' is already taken.");
            }
            vm.Rename(command.Name!);
        }
        if (changesStopTimeout)
        {
            vm.ChangeStopTimeout(command.StopTimeoutSeconds!.Value);
        }
        await repo.SaveAsync(ct);
    }
}
