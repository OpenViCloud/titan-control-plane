using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionRuntimeAggregate = Titan.Domain.Aggregates.FunctionRuntime.FunctionRuntime;

namespace Titan.Application.Commands.Catalog.FunctionRuntime;

// The runtime reconciler's observation of one function cluster (titan-ops architecture/resource-lifecycle.md#callback-routes):
// it records what was seen and ends no sync. Authenticated by X-Internal-Token at the endpoint; the runtime's row is
// locked like every lifecycle handler's.
public class ObserveFunctionRuntimeCommandHandler(IFunctionRuntimeRepository repo, FunctionClusterOptions clusters)
{
    public async Task HandleAsync(ObserveFunctionRuntimeCommand command, CancellationToken ct = default)
    {
        // The aggregate records an observation for any cluster it is handed; whether it is a function cluster is checked here.
        clusters.ClusterOf(command.ClusterId);

        FunctionRuntimeAggregate runtime = await repo.FindByIdForUpdateAsync(command.RuntimeId, ct)
            ?? throw new NotFoundException("FUNCTION_RUNTIME_NOT_FOUND", "Function runtime not found.");

        if (command.Exists)
        {
            runtime.MarkObservedIn(command.ClusterId);
        }
        else
        {
            runtime.MarkNotFoundIn(command.ClusterId);
        }

        await repo.SaveAsync(ct);
    }
}
