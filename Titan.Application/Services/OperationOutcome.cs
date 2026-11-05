namespace Titan.Application.Services;

/// How the worker says an operation's command ended (titan-ops architecture/resource-lifecycle.md#shape). Done: the
/// provider did what was asked, so the handler calls the resource's FinishOperation. Failed: the command gave up, so it
/// calls FailOperation, and desired goes back to current according to the operation's kind.
public enum OperationOutcome
{
    Done,
    Failed
}
