namespace Titan.Application.Commands.Compute.VmKeyAddition.Contracts;

// The report of a key addition operation's own command (titan-ops architecture/resource-lifecycle.md#callback-routes);
// the three ids come from the route. Outcome is "Done" or "Failed"; RestartNeeded only arrives with Done.
public record EndVmKeyAdditionRequestCommand(
    Guid VmId,
    Guid KeyAdditionId,
    Guid RequestId,
    string Outcome,
    bool? RestartNeeded = null,
    string? Reason = null,
    string? Detail = null);
