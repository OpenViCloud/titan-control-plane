namespace Titan.Domain.Values;

/// How an operation ended, in the report of the command that carried it out
/// (titan-ops architecture/resource-lifecycle.md#shape). Done: the provider did what was asked. Failed: the command gave
/// up, so desired goes back to current according to the operation's kind.
public enum OperationOutcome
{
    Done,
    Failed
}
