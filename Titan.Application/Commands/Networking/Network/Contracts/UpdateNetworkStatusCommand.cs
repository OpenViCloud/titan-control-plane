namespace Titan.Application.Commands.Networking.Network.Contracts;

// The worker's callback payload: an observation, but it writes, so it is a command. The route carries the id; the
// body carries the rest.
//
// Report says which of four reports this is (titan-ops features/network/design.md#network-reconciler): Observed (the
// reconciler: Exists, and Status — the Incus network status word for word — when it exists), Created and Deleted
// (the create and delete commands; Created brings the id and address), and a command's give-up (Reason/Detail; the
// create's may carry the id and address).
public record UpdateNetworkStatusCommand(
    Guid NetworkId,
    string Report,
    bool? Exists = null,
    string? Status = null,
    string? ExternalId = null,
    string? Cidr = null,
    string? Gateway = null,
    string? Reason = null,
    string? Detail = null);
