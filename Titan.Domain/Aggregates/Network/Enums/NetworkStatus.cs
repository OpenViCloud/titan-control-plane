namespace Titan.Domain.Aggregates.Network;

/// The one status of a network, the only one the API shows (titan-ops features/network/design.md#network-lifecycle). Two
/// say an operation is waiting; five are the Incus network status the network rests in (Network.IsIncusStatus, the only
/// ones Network.InstanceStatus can be), `Unknown` among them: Incus has that word too, and the report that read none
/// ends in it as well; the last three say what is not a resting network.
public enum NetworkStatus
{
    Creating,
    Deleting,
    Pending,
    Created,
    Errored,
    Unavailable,
    // The Incus word, and the status after a report in which the worker read none; the next observation writes the real one.
    Unknown,
    // The reconciler did not find the network; never Deleted, which only the delete command's Done writes.
    NotFound,
    // The create gave up before Incus held a network.
    Failed,
    Deleted
}
