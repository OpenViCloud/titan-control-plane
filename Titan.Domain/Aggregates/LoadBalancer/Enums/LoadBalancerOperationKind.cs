namespace Titan.Domain.Aggregates.LoadBalancer;

/// The kinds of operation a load balancer can wait on, one at a time (titan-ops architecture/resource-lifecycle.md#kinds).
/// Stored with the operation's id; the id says which operation a report ends, the kind says what ending it does. Its
/// listener and backend rows carry no operation of their own.
public enum LoadBalancerOperationKind
{
    Create,
    Update,
    Delete
}
