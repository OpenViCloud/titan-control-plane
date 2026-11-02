namespace Titan.Application.Commands.Compute.Vm.Contracts;

// The report of a VM request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes). Both ids come
// from the route: the request id is the one the command carries, so a late report for an older request names that
// request and not the one waiting now. Outcome is "Done" or "Failed"; the rest is what the worker saw.
public record EndVmRequestCommand(
    Guid VmId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    VmObservationReport? Observed = null,
    string? Reason = null,
    string? Detail = null);

// The instance as the worker read it: the Incus status word for word
// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim) and its addresses.
public record VmObservationReport(string Status, string? IpAddress = null, string? PublicIpv6Address = null);
