using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Tests;

// A test that is not about creation still builds through the real factories, the same way the handlers do. A state a
// factory cannot reach is reached by the same steps the provider's reports take (ProvisionedOnTheProvider).
internal static class TestAggregates
{
    private const int MoreThanAnyTestAdds = 1000;

    internal static NetworkAggregate Network(
        Tenant owner, string name, string clusterId,
        FirewallPolicy defaultIngressPolicy = FirewallPolicy.Allow,
        FirewallPolicy defaultEgressPolicy = FirewallPolicy.Allow,
        string? requestedCidr = null)
    {
        Ipv4Cidr? requested = null;
        if (requestedCidr != null)
        {
            requested = Ipv4Cidr.Parse(requestedCidr);
        }
        return NetworkAggregate.Create(
            owner, 0, 0, NetworkName.Parse(name), clusterId, requested, false, defaultIngressPolicy, defaultEgressPolicy);
    }

    internal static VmSnapshot WithSnapshot(this Vm vm, string name) => vm.AddSnapshot(Roomy(), name).Snapshot;

    internal static VolumeSnapshot WithSnapshot(this VolumeAggregate volume, string name) => volume.AddSnapshot(Roomy(), name).Snapshot;

    internal static FirewallRule WithFirewallRule(
        this NetworkAggregate network, FirewallDirection direction, FirewallProtocol protocol, int? startPort, int? endPort, string cidr)
    {
        PortRange? ports = null;
        if (startPort != null && endPort != null)
        {
            ports = PortRange.Of(startPort.Value, endPort.Value);
        }
        return network.AddFirewallRule(Roomy(), direction, protocol, ports, FirewallCidr.Parse(cidr)).Rule;
    }

    private static Tenant Roomy()
    {
        Tenant tenant = TestTenant.WithRoomToSpare();
        tenant.UpdateQuota(TenantQuota.Create(
            MoreThanAnyTestAdds, MoreThanAnyTestAdds, MoreThanAnyTestAdds, MoreThanAnyTestAdds, MoreThanAnyTestAdds,
            MoreThanAnyTestAdds, MoreThanAnyTestAdds, MoreThanAnyTestAdds, MoreThanAnyTestAdds));
        return tenant;
    }
}
