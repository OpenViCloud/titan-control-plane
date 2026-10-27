namespace Titan.Application.Queries.Networking.Network.Contracts;

// The network reconciler asks for the networks of one cluster it must observe.
public record ListReconcilableNetworksQuery(string ClusterId);
