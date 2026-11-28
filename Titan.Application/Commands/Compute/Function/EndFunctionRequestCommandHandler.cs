using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Application.Services;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Application.Commands.Compute.Function;

// The function worker's report that a function request's command ended
// (titan-ops architecture/resource-lifecycle.md#callback-routes). Authenticated by X-Internal-Token at the endpoint,
// acting for no tenant. The row is locked so a request made meanwhile is ordered before or after this report. A report
// the function is not waiting on is ignored and still answers 204: the worker has nothing to retry.
public class EndFunctionRequestCommandHandler(IFunctionRepository repo)
{
    public async Task HandleAsync(EndFunctionRequestCommand command, CancellationToken ct = default)
    {
        // Parsed before the function is loaded: a malformed report is the worker's bug, whatever the function.
        OperationOutcome outcome = OperationOutcomes.Parse(command.Outcome);

        FunctionAggregate function = await repo.FindByIdForUpdateAsync(command.FunctionId, ct)
            ?? throw new NotFoundException("FUNCTION_NOT_FOUND", "Function not found.");

        // The status says which operation waits; a report of the other one names an id not waiting and is ignored.
        bool ended;
        if (function.Status == FunctionStatus.Deleting)
        {
            if (outcome == OperationOutcome.Done)
            {
                ended = function.DeleteSucceeded(command.RequestId);
            }
            else
            {
                ended = function.DeleteFailed(command.RequestId, command.Reason, command.Detail);
            }
        }
        else if (outcome == OperationOutcome.Done)
        {
            ended = function.DeploySucceeded(command.RequestId, command.ExternalId);
        }
        else
        {
            ended = function.DeployFailed(command.RequestId, command.ExternalId, command.Reason, command.Detail);
        }
        if (!ended)
        {
            return;
        }

        await repo.SaveAsync(ct);
    }
}
