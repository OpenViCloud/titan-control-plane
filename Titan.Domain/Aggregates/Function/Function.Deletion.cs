using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Function;

public partial class Function
{
    /// A function the cluster was never told of (no Service name) is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally). Any other waits for the delete command, remembering
    /// what the cluster held (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refused while a
    /// deploy waits, and as a repeat, which the handler recognises first.
    public OperationTicket? Delete()
    {
        if (Status == FunctionStatus.Deleting)
            throw new BusinessException("INVALID_STATUS", "A delete is already in progress for this function.");
        if (IsDeleted)
            throw new BusinessException("INVALID_STATUS", "The function is already deleted.");
        if (Status == FunctionStatus.Deploying)
            throw new BusinessException("INVALID_STATUS", "A deploy is still waiting for this function.");
        if (ExternalId is null)
        {
            Status = FunctionStatus.Deleted;
            return null;
        }
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, SeenAtRest());
        Status = FunctionStatus.Deleting;
        return OperationTicket.Of(operationId, ExternalId);
    }

    /// The delete command ended Done: the Service and its Secrets are gone from the cluster. Every version's code is now
    /// the cleanup job's. Ignored, returning false, when it names another operation than the waiting delete.
    public bool DeleteSucceeded(Guid operationId)
    {
        if (!IsWaitingOn(operationId, FunctionStatus.Deleting))
        {
            return false;
        }
        Operation = Operation.Done();
        Status = FunctionStatus.Deleted;
        return true;
    }

    /// The delete command gave up: the function rests where it was, with the reason, and the next delete is a new
    /// operation. Ignored, returning false, when it names another operation than the waiting delete.
    public bool DeleteFailed(Guid operationId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, FunctionStatus.Deleting))
        {
            return false;
        }
        FunctionStatus resting = RestingStatus();
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        return true;
    }
}
