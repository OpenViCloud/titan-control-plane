using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Volume;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Tests;

// A test that is not about creation still builds through the real factories, the same way the handlers do. A state a
// factory cannot reach is reached by the same steps the provider's reports take (Fakes/ProvisionedOnTheProvider.cs).
internal static class TestAggregates
{
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
            owner.Id, NetworkName.Parse(name), clusterId, requested, false, defaultIngressPolicy, defaultEgressPolicy);
    }

    internal static VmSnapshot WithSnapshot(this Vm vm, string name)
    {
        Guid snapshotId = Guid.NewGuid();
        vm.AddSnapshot(snapshotId, Guid.NewGuid(), name);
        VmSnapshot snapshot = vm.Snapshots.Single(s => s.Id == snapshotId);
        snapshot.HeldBy(vm);
        return snapshot;
    }

    internal static VolumeSnapshot WithSnapshot(this VolumeAggregate volume, string name)
    {
        Guid snapshotId = Guid.NewGuid();
        volume.AddSnapshot(snapshotId, name);
        return volume.Snapshots.Single(s => s.Id == snapshotId);
    }

    internal static FirewallRule WithFirewallRule(
        this NetworkAggregate network, FirewallDirection direction, FirewallProtocol protocol, PortRange? ports, FirewallCidr cidr)
    {
        Guid ruleId = Guid.NewGuid();
        network.AddFirewallRule(ruleId, direction, protocol, ports, cidr);
        return network.FirewallRules.Single(r => r.Id == ruleId);
    }

    internal static FirewallRule WithFirewallRule(
        this NetworkAggregate network, FirewallDirection direction, FirewallProtocol protocol, int? startPort, int? endPort, string cidr)
    {
        PortRange? ports = null;
        if (startPort != null && endPort != null)
        {
            ports = PortRange.Of(startPort.Value, endPort.Value);
        }
        return network.WithFirewallRule(direction, protocol, ports, FirewallCidr.Parse(cidr));
    }
}
