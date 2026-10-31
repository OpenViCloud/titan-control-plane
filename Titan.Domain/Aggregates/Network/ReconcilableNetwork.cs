using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// One row of the network reconciler's list (titan-ops features/network/user-stories.md#us-net-intent-04): the columns themselves, not an aggregate, so
/// a stored name or requested CIDR that breaks its rule does not cost the worker this list for the cluster.
public record ReconcilableNetwork(Guid Id, string ExternalId, NetworkStatus? CurrentState, CurrentExistence CurrentExistence);
