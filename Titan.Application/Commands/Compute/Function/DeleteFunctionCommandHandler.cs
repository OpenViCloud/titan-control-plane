using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Application.Commands.Compute.Function;

// The zip and the image of every version stay until the delete is Done; the cleanup job removes them then
// (titan-ops features/ovc-function/design.md#function-lifecycle).
public class DeleteFunctionCommandHandler(IFunctionRepository repo, ICurrentUser currentUser, IProviderCommandQueue commands)
{
    public async Task HandleAsync(DeleteFunctionCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        // Locked, so two deletes take turns and the second sees the first waiting
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        FunctionAggregate function = await ComputeOwnership.RequireFunctionForUpdateAsync(repo, tenantId, command.FunctionId, ct);

        // A repeat is answered like the first request: nothing written, nothing sent
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
        if (function.IsDeletedOrDeleting)
        {
            return;
        }

        // A function never named on the cluster is deleted here and gets no ticket
        // (titan-ops architecture/desired-and-current.md#delete-locally).
        OperationTicket? ticket = function.Delete();
        if (ticket != null)
        {
            await commands.EnqueueFunctionDeleteAsync(function, ticket.OperationId, ct);
        }
        await repo.SaveAsync(ct);
    }
}
