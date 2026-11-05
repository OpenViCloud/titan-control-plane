using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;


namespace Titan.Tests.Domain;

// The two cross-aggregate checks a load balancer create and update go through: the network (Networking) and each
// backend VM (Compute). The network hands out its provider id only past its own check; the VM checks itself, and its
// GetExternalId throws without one (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public class LoadBalancerTargetTests
{
    private static readonly Tenant Owner = TestTenant.WithRoomToSpare();

    private static Network UsableNetwork(bool withPublicIpv6)
    {
        Network network;
        if (withPublicIpv6)
        {
            network = TestNetworks.CreatingWithPublicIpv6(Owner, "net-1", "cluster-a");
        }
        else
        {
            network = Network.Create(Owner.Id, NetworkName.Parse("net-1"), "cluster-a", null, wantsPublicIpv6: false);
        }
        network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    [Fact]
    public void Network_WithItsSlash64_AndUsable_HandsOutItsId()
    {
        Network network = UsableNetwork(true);

        Assert.Equal("net-ext", network.ProviderIdForLoadBalancer());
    }

    // titan-ops features/load-balancer/design.md#lb-needs-public-ipv6: Incus refuses an isolated OVN network.
    [Fact]
    public void Network_WithoutPublicIpv6_IsRefused_BeforeItsState()
    {
        Network noIpv6 = Network.Create(Owner.Id, NetworkName.Parse("net-1"), "cluster-a", null, false);

        Assert.Equal("LOAD_BALANCER_NEEDS_PUBLIC_IPV6",
            Assert.Throws<BusinessException>(() => noIpv6.ProviderIdForLoadBalancer()).Code);
    }

    [Fact]
    public void Network_NotYetCreated_OrBeingDeleted_IsRefused()
    {
        Network creating = TestNetworks.CreatingWithPublicIpv6(Owner, "net-1", "cluster-a");
        Network deleting = UsableNetwork(true);
        deleting.Delete();

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => creating.ProviderIdForLoadBalancer()).Code);
        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => deleting.ProviderIdForLoadBalancer()).Code);
    }

    private static Vm VmOnIncus(Guid networkId)
    {
        var vm = Vm.Create(Owner.Id, networkId, "vm-1", "m", "tpl", "cluster-a", 20);
        vm.MarkRunning("vmabc", "10.0.0.5");
        return vm;
    }

    [Fact]
    public void Vm_OnTheNetwork_OnIncus_IsABackend_NamedByItsInstance()
    {
        Guid networkId = Guid.NewGuid();
        Vm vm = VmOnIncus(networkId);

        vm.EnsureLoadBalancerCanSendTo(networkId);

        Assert.Equal("vmabc", vm.ExternalId);
    }

    // Stopped is fine: the health check takes it out of rotation (titan-ops features/load-balancer/design.md#lb-and-vm-network).
    [Fact]
    public void Vm_Stopped_IsStillABackend()
    {
        Guid networkId = Guid.NewGuid();
        Vm vm = VmOnIncus(networkId);
        vm.MarkStopped();

        vm.EnsureLoadBalancerCanSendTo(networkId);

        Assert.Equal("vmabc", vm.ExternalId);
    }

    [Fact]
    public void Vm_OnAnotherNetwork_NotOnIncus_OrDeletePressed_IsRefused()
    {
        Guid networkId = Guid.NewGuid();
        Vm elsewhere = VmOnIncus(Guid.NewGuid());
        Vm creating = Vm.Create(Owner.Id, networkId, "vm-2", "m", "tpl", "cluster-a", 20);
        Vm deleting = VmOnIncus(networkId);
        deleting.Delete(Guid.NewGuid());

        foreach (Vm vm in new[] { elsewhere, creating, deleting })
        {
            Assert.Equal("LOAD_BALANCER_BACKEND_INVALID",
                Assert.Throws<BusinessException>(() => vm.EnsureLoadBalancerCanSendTo(networkId)).Code);
        }
    }
}
