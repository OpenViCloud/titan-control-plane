using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Function;

public partial class Function
{
    /// The version waits on its deploy or already runs, and no delete was asked: a later deploy of it is a repeat,
    /// answered like the first (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A version that
    /// runs while the cluster no longer holds the Service (NotFound) is not: deploying it again puts the Service back.
    public bool IsDeployWaitingOrDone(Guid versionId)
    {
        if (Status == FunctionStatus.Deploying)
        {
            return DeployingVersionId == versionId;
        }
        return Status == FunctionStatus.Active && RunningVersionId == versionId;
    }

    /// Makes the version the one the Knative Service serves (titan-ops features/ovc-function/design.md#function-versions):
    /// the deploy waits on its command, remembering what the cluster held. Refused while another deploy or a delete
    /// waits, once the function is gone, and as a repeat, which the handler recognises first. Returns the operation's id
    /// for the command's key and payload.
    public Guid Deploy(Guid versionId)
    {
        FunctionVersion? version = FindKeptVersion(versionId);
        if (version == null)
        {
            throw new NotFoundException("FUNCTION_VERSION_NOT_FOUND", "Function version not found.");
        }
        EnsureNotDeletedOrDeleting();
        if (Status == FunctionStatus.Deploying)
        {
            throw new BusinessException("INVALID_STATUS", "A deploy is already waiting for this function.");
        }
        if (Status == FunctionStatus.Active && RunningVersionId == versionId)
        {
            throw new BusinessException("INVALID_STATUS", "This version already runs.");
        }
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, SeenAtRest());
        DeployingVersionId = version.Id;
        Status = FunctionStatus.Deploying;
        return operationId;
    }

    /// The deploy command ended Done: the revision of the version is Ready and takes every call, so the version runs,
    /// and an older failure is cleared. A Done must leave the function named on the cluster, or nothing would be left to
    /// delete it through: without a Service name it is refused before anything is written (a worker that sends it has a
    /// bug, and does not retry a 422). Ignored, returning false, when it names another operation than the waiting deploy.
    public bool DeploySucceeded(Guid operationId, string? externalId)
    {
        if (!IsWaitingOn(operationId, FunctionStatus.Deploying))
        {
            return false;
        }
        bool doneWithoutAnId = ExternalId == null && string.IsNullOrEmpty(externalId);
        if (doneWithoutAnId)
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a deploy ends Done.");
        }
        RecordTheService(externalId);
        RunningVersionId = DeployingVersionId;
        DeployingVersionId = null;
        Operation = Operation.Done();
        Status = FunctionStatus.Active;
        DiscardBeyondTheKept(DateTimeOffset.UtcNow);
        return true;
    }

    /// The deploy command gave up: the function rests where it was (Inactive while no version ever ran, else as the
    /// cluster was last seen holding the version that still runs), with the reason. A Service name the report brings is
    /// kept, so a later delete reaches it. Ignored, returning false, when it names another operation than the waiting
    /// deploy.
    public bool DeployFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, FunctionStatus.Deploying))
        {
            return false;
        }
        RecordTheService(externalId);
        FunctionStatus resting = RestingStatus();
        DeployingVersionId = null;
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        Status = resting;
        DiscardBeyondTheKept(DateTimeOffset.UtcNow);
        return true;
    }
}
