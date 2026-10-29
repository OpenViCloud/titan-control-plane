namespace Titan.Application.Commands.Compute.LoadBalancer.Contracts;

// Only the id (titan-ops architecture/http-contract.md#create-returns-id).
public record CreateLoadBalancerCommandResult(Guid LoadBalancerId);
