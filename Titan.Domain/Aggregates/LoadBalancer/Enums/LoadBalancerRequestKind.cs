namespace Titan.Domain.Aggregates.LoadBalancer;

/// The kinds of request a load balancer can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds).
/// Stored with the request's id; the id says which request a report ends, the kind says what ending it does. Its
/// listener and backend rows carry no request of their own.
public enum LoadBalancerRequestKind
{
    Create,
    Update,
    Delete
}
