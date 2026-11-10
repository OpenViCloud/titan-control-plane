namespace Titan.Application.Services;

/// How the worker says an operation's command ended (titan-ops architecture/resource-lifecycle.md#shape). Done: the
/// provider did what was asked. Failed: the command gave up. The handler passes it to the resource's Succeeded or Failed
/// method of the status the resource waits in (OperationOutcomes.Parse reads the word).
public enum OperationOutcome
{
    Done,
    Failed
}
