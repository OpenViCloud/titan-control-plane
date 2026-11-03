namespace Titan.Application.Commands.Compute.VmSnapshot.Contracts;

// The report of a VM snapshot request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes); the
// three ids come from the route. Outcome is "Done" or "Failed".
public record EndVmSnapshotRequestCommand(
    Guid VmId,
    Guid SnapshotId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);
