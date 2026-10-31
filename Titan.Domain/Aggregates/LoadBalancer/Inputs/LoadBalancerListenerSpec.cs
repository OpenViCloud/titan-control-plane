namespace Titan.Domain.Aggregates.LoadBalancer;

/// One listener as the customer asks for it: Port on the VIP goes to TargetPort on every backend.
public sealed record LoadBalancerListenerSpec(LoadBalancerProtocol Protocol, int Port, int TargetPort);
