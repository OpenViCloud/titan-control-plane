using Titan.Domain.Aggregates.Compute.LoadBalancer;

namespace Titan.Tests.Domain;

// titan-ops features/load-balancer/design.md#vip-in-network-64: the VIP is interface id 0:0:1:n of the network's own /64.
// Incus takes a VIP equal to the gateway or the router as readily as a free one, so the shape is Titan's guard.
public class LoadBalancerVipTests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";

    [Theory]
    [InlineData(1, "2001:41d0:d00:d405::1:1")]
    [InlineData(255, "2001:41d0:d00:d405::1:ff")]
    [InlineData(65535, "2001:41d0:d00:d405::1:ffff")]
    public void For_NumberN_IsInterfaceId_0_0_1_N_OfTheSubnet(int index, string vip)
    {
        LoadBalancerVip address = LoadBalancerVip.For(Subnet, index);

        Assert.Equal(index, address.Index);
        Assert.Equal(vip, address.Address);
    }

    // ::1 is the network's gateway; an EUI-64 address carries ff:fe in the middle of its interface id.
    [Fact]
    public void For_NeverGivesTheGateway_NorAnEui64Address()
    {
        foreach (int index in new[] { 1, 2, 0xfe, 0xff, 0x100, 0xfffe, 0xffff })
        {
            string address = LoadBalancerVip.For(Subnet, index).Address;

            Assert.NotEqual("2001:41d0:d00:d405::1", address);
            byte[] bytes = System.Net.IPAddress.Parse(address).GetAddressBytes();
            Assert.False(bytes[11] == 0xff && bytes[12] == 0xfe, address);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void For_OutsideOneTo65535_IsRefused(int index)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LoadBalancerVip.For(Subnet, index));
    }

    [Fact]
    public void FirstFree_TakesTheLowestNumberNoLiveLoadBalancerOfTheNetworkHolds()
    {
        LoadBalancerVip? free = LoadBalancerVip.FirstFree(Subnet, new HashSet<int> { 1, 2, 4 });

        Assert.NotNull(free);
        Assert.Equal(3, free.Index);
        Assert.Equal("2001:41d0:d00:d405::1:3", free.Address);
    }

    [Fact]
    public void FirstFree_WhenEveryNumberIsTaken_IsNull()
    {
        Assert.Null(LoadBalancerVip.FirstFree(Subnet, Enumerable.Range(1, 65535).ToHashSet()));
    }
}
