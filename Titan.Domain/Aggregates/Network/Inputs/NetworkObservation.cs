namespace Titan.Domain.Aggregates.Network;

/// What the worker read of the network on Incus while ending a request: the status word as Incus wrote it
/// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim) and the IPv4 range and gateway, the ones the
/// create asked for or Incus picked. Any of them may be missing: a worker from before the request routes sends no
/// status, and a give-up that could not read the range back sends no address.
public sealed record NetworkObservation(NetworkStatus? Status, string? Cidr, string? Gateway);
