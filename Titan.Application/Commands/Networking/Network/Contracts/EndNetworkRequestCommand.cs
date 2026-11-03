namespace Titan.Application.Commands.Networking.Network.Contracts;

// The report of a network request's own command (titan-ops architecture/resource-lifecycle.md#callback-routes). Both ids
// come from the route: the request id is the one the command carries. Outcome is "Done" or "Failed"; Status, Cidr and
// Gateway are what the worker read of the network, each absent when it did not read it.
public record EndNetworkRequestCommand(
    Guid NetworkId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    string? Status = null,
    string? Cidr = null,
    string? Gateway = null,
    string? Reason = null,
    string? Detail = null);
