using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class Network
{
    /// The range the customer asked for is checked against the public IPv6 choice here, an invariant of two of the network's own
    /// attributes (titan-ops architecture/code-structure.md#logic-placement): the uplink of a network with public IPv6
    /// routes only the private ranges (titan-ops infra/incus/design.md#ipv6-address-plan). The validator answers the
    /// same case with 400 first, so NETWORK_CIDR_NOT_PRIVATE is not reached over HTTP.
    public static Network Create(
        Guid tenantId, NetworkName name, string clusterId,
        Ipv4Cidr? chosenCidr, bool wantsPublicIpv6,
        FirewallPolicy defaultIngressPolicy = FirewallPolicy.Allow,
        FirewallPolicy defaultEgressPolicy = FirewallPolicy.Allow)
    {
        bool cidrOutsideThePrivateRanges = wantsPublicIpv6 && chosenCidr != null && !chosenCidr.IsInsideAPrivateRange;
        if (cidrOutsideThePrivateRanges)
        {
            throw new BusinessException("NETWORK_CIDR_NOT_PRIVATE",
                "The CIDR of a network with public IPv6 must be inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16.");
        }
        var network = new Network
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            ChosenCidr = chosenCidr,
            ClusterId = clusterId,
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            DefaultIngressPolicy = defaultIngressPolicy,
            DefaultEgressPolicy = defaultEgressPolicy,
            CreatedAt = DateTimeOffset.UtcNow
        };
        network.Operation = network.Operation.Asked(Guid.NewGuid(), NetworkOperationKind.Create, DateTimeOffset.UtcNow);
        return network;
    }

    /// The /64 of the route given to this network. The route keeps who holds it by the network's id, so the /64 comes
    /// after Create: once, and only while the create is waiting. Internal: NetworkRoutes gives the route with it.
    internal void ReceivePublicIpv6(string subnet)
    {
        if (HasPublicIpv6)
        {
            throw new InvalidOperationException($"Network {Id} already has a public IPv6 /64.");
        }
        if (!CreateIsWaiting)
        {
            throw new InvalidOperationException($"Network {Id} is not waiting for its create, so it takes no new /64.");
        }
        PublicIpv6Subnet = subnet;
    }

    /// The create command ended Done: records what the worker saw, the id first, and the network waits on nothing. A report
    /// that names another operation than the create the network waits on is ignored and returns false: a Done that arrived
    /// twice, a report for a network already Deleted. A null status, cidr or gateway: the worker did not read it.
    internal bool CreateSucceeded(Guid operationId, string? externalId, NetworkStatus? status, string? cidr, string? gateway)
    {
        if (!IsWaitingOn(operationId, NetworkOperationKind.Create))
        {
            return false;
        }
        EnsureTheDoneLeavesAnId(externalId);
        RecordWhatTheWorkerSaw(externalId, status, cidr, gateway);
        Operation = Operation.Done();
        return true;
    }

    /// The create command gave up: records what the worker saw and the reason; desired stays, since a failed create may be
    /// deleted, not retried. Incus never had the network when the report brings no id, so nothing holds the /64 there;
    /// keeping it here would hold one of the cluster's 255 for as long as the network lives. Ignored, returning false, when
    /// it names another operation than the create the network waits on.
    internal bool CreateFailed(Guid operationId, string? externalId, NetworkStatus? status, string? cidr, string? gateway,
        string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, NetworkOperationKind.Create))
        {
            return false;
        }
        RecordWhatTheWorkerSaw(externalId, status, cidr, gateway);
        bool incusNeverHadIt = ExternalId is null;
        if (incusNeverHadIt && HasPublicIpv6)
        {
            PublicIpv6Subnet = null;
        }
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        return true;
    }
}
