using Titan.Application.Abstractions;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Application.Commands.Compute.Vm;


// A command that enqueues nothing: renaming is Titan-side only, the provider never hears about it.
// It still belongs in Commands/ because it WRITES — the split is read-vs-write, not remote-vs-local.
public class RenameVmCommandHandler(IVmRepository repo, ICurrentUser currentUser)
{
    // Returns nothing on purpose (titan-ops architecture/code-structure.md#no-contract-reuse): a caller that wants the
    // renamed VM asks the query side for it.
    public async Task HandleAsync(RenameVmCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        Domain.Aggregates.Vm.Vm vm = await ComputeOwnership.RequireVmAsync(repo, tenantId, command.VmId, ct);

        // The name asked for is already the name — done, nothing to write
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        bool alreadyNamedSo = command.Name == vm.Name;
        if (alreadyNamedSo)
        {
            return;
        }

        bool nameTaken = await repo.ExistsByNameAsync(tenantId, command.Name, ct);
        if (nameTaken)
        {
            throw new ConflictException("VM_NAME_TAKEN", $"VM name '{command.Name}' is already taken.");
        }

        vm.Rename(command.Name);
        await repo.SaveAsync(ct);
    }
}
