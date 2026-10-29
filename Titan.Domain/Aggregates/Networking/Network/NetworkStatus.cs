namespace Titan.Domain.Aggregates.Networking.Network;

/// The Incus network status, word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim):
/// the five api.NetworkStatus* constants of Incus 7.0.1. Only an observation writes it; Network.CurrentState is null
/// while Incus has no network. Whether it exists is Network.CurrentExistence.
public enum NetworkStatus
{
    Pending,
    Created,
    Errored,
    Unknown,
    Unavailable
}
