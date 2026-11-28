using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Application.Commands.Compute.Function;

// The function reconciler's observation of one function (titan-ops architecture/resource-lifecycle.md#callback-routes):
// what the cluster holds; no request ends here (titan-ops architecture/resource-lifecycle.md#invariants). Authenticated
// by X-Internal-Token at the endpoint; locked like every lifecycle handler.
public class ObserveFunctionCommandHandler(IFunctionRepository repo)
{
    public async Task HandleAsync(ObserveFunctionCommand command, CancellationToken ct = default)
    {
        FunctionAggregate function = await repo.FindByIdForUpdateAsync(command.FunctionId, ct)
            ?? throw new NotFoundException("FUNCTION_NOT_FOUND", "Function not found.");

        if (command.Exists)
        {
            function.MarkObserved();
        }
        else
        {
            function.MarkNotFound();
        }
        await repo.SaveAsync(ct);
    }
}
