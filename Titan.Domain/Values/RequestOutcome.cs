namespace Titan.Domain.Values;

/// How a request ended, in the report of the command that carried it out
/// (titan-ops architecture/resource-lifecycle.md#shape). Done: the provider did what was asked. Failed: the command gave
/// up, so desired goes back to current according to the request's kind.
public enum RequestOutcome
{
    Done,
    Failed
}
