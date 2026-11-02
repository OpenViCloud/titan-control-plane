namespace Titan.Domain.Aggregates.Vm;

/// What the worker saw of the instance on Incus: the status word for word
/// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim) and its addresses. Carried by the reconciler's
/// observation and by a command's report alike; an instance the worker did not find has no observation.
public sealed record VmObservation(VmState Status, string? IpAddress, string? PublicIpv6Address);
