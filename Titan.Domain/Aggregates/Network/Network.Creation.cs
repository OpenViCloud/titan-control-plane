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
            Status = NetworkStatus.Creating,
            DefaultIngressPolicy = defaultIngressPolicy,
            DefaultEgressPolicy = defaultEgressPolicy,
            CreatedAt = DateTimeOffset.UtcNow
        };
        network.Operation = network.Operation.Asked(Guid.NewGuid(), DateTimeOffset.UtcNow, null);
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

    /// The create command ended Done: records the id and the addresses the worker saw, and the network waits on nothing. Its
    /// status is the Incus status the report carries, Unknown when the worker read none (the next observation writes the
    /// real one). A report that names another operation than the create the network waits on is ignored and returns false:
    /// a Done that arrived twice, a report for a network already Deleted.
    internal bool CreateSucceeded(Guid operationId, string? externalId, NetworkStatus? status, string? cidr, string? gateway)
    {
        if (!IsWaitingOn(operationId, NetworkStatus.Creating))
        {
            return false;
        }
        EnsureTheDoneLeavesAnId(externalId);
        EnsureNotForeign(status);
        RecordTheIdAndAddresses(externalId, cidr, gateway);
        Operation = Operation.Done();
        Status = status ?? NetworkStatus.Unknown;
        return true;
    }

    /// The create command gave up: records the reason. Before Incus held a network (the report brings no id) it is Failed, and
    /// nothing holds the /64 any more: keeping it would hold one of the cluster's 255 for as long as the network lives.
    /// After (the report names it), the network rests in the Incus status the report carries, else the one it held, else
    /// Unknown; desired stays, since a failed create may be deleted, not retried. Ignored, returning false, when it names
    /// another operation than the create the network waits on.
    internal bool CreateFailed(Guid operationId, string? externalId, NetworkStatus? status, string? cidr, string? gateway,
        string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, NetworkStatus.Creating))
        {
            return false;
        }
        EnsureNotForeign(status);
        RecordTheIdAndAddresses(externalId, cidr, gateway);
        NetworkStatus resting = status ?? InstanceStatus ?? NetworkStatus.Unknown;
        bool incusNeverHadIt = ExternalId is null;
        if (incusNeverHadIt && HasPublicIpv6)
        {
            PublicIpv6Subnet = null;
        }
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        if (incusNeverHadIt)
        {
            Status = NetworkStatus.Failed;
        }
        else
        {
            Status = resting;
        }
        return true;
    }
}
