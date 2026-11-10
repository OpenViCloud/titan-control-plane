using Titan.Application.Exceptions;

namespace Titan.Application.Services;

/// The outcome word of every request report the worker sends (titan-ops architecture/resource-lifecycle.md#callback-routes):
/// one parse for every kind, so a new word is refused the same way everywhere.
public static class OperationOutcomes
{
    public static OperationOutcome Parse(string? outcome)
    {
        switch (outcome)
        {
            case "Done":
                return OperationOutcome.Done;
            case "Failed":
                return OperationOutcome.Failed;
            default:
                throw new BadRequestException("INVALID_STATUS", $"Unknown request outcome: '{outcome}'");
        }
    }
}
