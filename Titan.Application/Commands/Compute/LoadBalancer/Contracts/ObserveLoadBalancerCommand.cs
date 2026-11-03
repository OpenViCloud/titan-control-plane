namespace Titan.Application.Commands.Compute.LoadBalancer.Contracts;

// The reconciler's observation of one load balancer: whether its VIP is on its network on Incus. LoadBalancerId comes
// from the route.
public record ObserveLoadBalancerCommand(Guid LoadBalancerId, bool Exists);
