using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;
using Titan.Application.Options;
using Titan.Application.Services;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionRuntimeAggregate = Titan.Domain.Aggregates.FunctionRuntime.FunctionRuntime;

namespace Titan.Application.Commands.Catalog.FunctionRuntime;

// The worker's report that a runtime sync's command in one function cluster ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes). Authenticated by X-Internal-Token at the endpoint. The
// runtime's row is locked, so a sync-all meanwhile is ordered before or after this report. A report the cluster is not
// waiting on is ignored and still answers 204: the worker has nothing to retry.
public class EndFunctionRuntimeSyncRequestCommandHandler(IFunctionRuntimeRepository repo, FunctionClusterOptions clusters)
{
    public async Task HandleAsync(EndFunctionRuntimeSyncRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before anything is loaded: a malformed report is the worker's bug, whatever the runtime.
        OperationOutcome outcome = OperationOutcomes.Parse(command.Outcome);

        // The aggregate records a report for any cluster it is handed; whether it is a function cluster is checked here.
        clusters.ClusterOf(command.ClusterId);

        FunctionRuntimeAggregate runtime = await repo.FindByIdForUpdateAsync(command.RuntimeId, ct)
            ?? throw new NotFoundException("FUNCTION_RUNTIME_NOT_FOUND", "Function runtime not found.");

        bool ended;
        if (outcome == OperationOutcome.Done)
        {
            ended = runtime.SyncSucceededIn(command.ClusterId, command.RequestId);
        }
        else
        {
            ended = runtime.SyncFailedIn(command.ClusterId, command.RequestId, command.Reason, command.Detail);
        }
        if (!ended)
        {
            return;
        }

        await repo.SaveAsync(ct);
    }
}
