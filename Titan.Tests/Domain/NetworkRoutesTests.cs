using System.Reflection;
using Moq;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;

namespace Titan.Tests.Domain;

// titan-ops features/network/design.md#ipv6-public: a live network holds its route while it holds its /64, it gets both
// at once and lets go of the route when it lets go of the /64 or is deleted, and NetworkRoutes is the only way to do either. Silent about the lock on the free routes;
// ClusterIpv6RouteDbTests runs two creates at once on Postgres.
public class NetworkRoutesTests
{
    private const string Block56 = "2001:41d0:d00:d400::/56";

    private readonly Mock<IClusterIpv6RouteRepository> _routes = new();
    private readonly List<ClusterIpv6Route> _rows = Enumerable.Range(1, 255).Select(n => ClusterIpv6Route.Create("cluster-1", n)).ToList();

    public NetworkRoutesTests()
    {
        _routes.Setup(r => r.LockFreeAsync("cluster-1", default)).ReturnsAsync(() => _rows.Where(route => route.IsFree).ToList());
        _routes.Setup(r => r.FindHeldByAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((Guid networkId, CancellationToken _) => _rows.SingleOrDefault(route => route.NetworkId == networkId));
    }

    private NetworkRoutes Routes() => new(_routes.Object);

    private static Network WantingPublicIpv6() =>
        Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "cluster-1", null, wantsPublicIpv6: true);

    private static Network WithoutPublicIpv6() =>
        Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "cluster-1", null, wantsPublicIpv6: false);

    private ClusterIpv6Route RouteOf(Network network) => _rows.Single(route => route.NetworkId == network.Id);

    [Fact]
    public async Task Give_HandsTheNetworkTheLowestFreeRoute_WithItsSubnet_AndReturnsItsRouter()
    {
        Network network = WantingPublicIpv6();

        string router = await Routes().GiveAsync(network, Block56);

        Assert.Equal(1, RouteOf(network).Number);
        Assert.Equal("2001:41d0:d00:d401::/64", network.PublicIpv6Subnet);
        Assert.Equal("2001:41d0:d00:d400::101", router);
    }

    [Fact]
    public async Task Give_WithNoFreeRoute_IsRefused_AndTheNetworkGetsNoSubnet()
    {
        foreach (ClusterIpv6Route route in _rows)
        {
            route.HandTo(Guid.NewGuid());
        }
        Network network = WantingPublicIpv6();

        BusinessException ex = await Assert.ThrowsAsync<BusinessException>(() => Routes().GiveAsync(network, Block56));

        Assert.Equal("IPV6_NOT_AVAILABLE", ex.Code);
        Assert.False(network.HasPublicIpv6);
    }

    [Fact]
    public async Task CreateGaveUpBeforeIncusHadIt_LetsGoOfTheSubnet_AndTheRouteGoesBack()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        ClusterIpv6Route route = RouteOf(network);

        await Routes().RecordCreateGaveUpAsync(network, "Network creation failed.", "raw");

        Assert.False(network.HasPublicIpv6);
        Assert.Equal("Network creation failed.", network.RequestFailureReason);
        Assert.True(route.IsFree);
        Assert.NotNull(route.ReleasedAt);
    }

    [Fact]
    public async Task CreateGaveUpAfterIncusHadIt_KeepsTheSubnet_AndTheRoute()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        network.MarkCreated("net-x", "10.0.0.0/24", "10.0.0.1");

        await Routes().RecordCreateGaveUpAsync(network, "Network creation failed.", "raw");

        Assert.True(network.HasPublicIpv6);
        Assert.False(RouteOf(network).IsFree);
    }

    [Fact]
    public async Task Deleted_TakesTheRouteBack_Once()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        ClusterIpv6Route route = RouteOf(network);
        network.MarkActive("net-x", "10.0.0.0/24", "10.0.0.1");

        NetworkDeletionOutcome first = await Routes().RecordDeletedAsync(network);
        DateTimeOffset? releasedAt = route.ReleasedAt;
        NetworkDeletionOutcome second = await Routes().RecordDeletedAsync(network);

        Assert.True(network.IsTerminal);
        Assert.True(route.IsFree);
        Assert.NotNull(releasedAt);
        Assert.Equal(releasedAt, route.ReleasedAt);
        Assert.False(first.FulfilledNetworkDelete); // no delete was waiting: MarkActive is the network on Incus, untouched
        Assert.False(second.FulfilledNetworkDelete);
    }

    [Fact]
    public async Task Deleted_ANetworkWithoutPublicIpv6_TouchesNoRoute()
    {
        Network network = WithoutPublicIpv6();
        network.MarkActive("net-x", "10.0.0.0/24", "10.0.0.1");

        await Routes().RecordDeletedAsync(network);

        Assert.True(network.IsTerminal);
        Assert.All(_rows, route => Assert.True(route.IsFree));
    }

    // A network whose create gave up before Incus let go of its /64 there; one still holding it would keep its route once
    // deleted locally, since no delete report follows. The reconciler only reports networks with a provider id, so no
    // path reaches this today.
    [Fact]
    public async Task DeleteLocally_ANetworkStillHoldingItsSubnet_IsRefused()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        network.MarkNotFound(); // ends the waiting create without a give-up, so the /64 stays

        BusinessException ex = Assert.Throws<BusinessException>(() => network.DeleteLocally());

        Assert.Equal("NETWORK_INVALID_STATE", ex.Code);
        Assert.False(network.IsTerminal);
    }

    // The compiler keeps Titan.Application off these, so NetworkRoutes is the only way a route and a /64 move.
    [Fact]
    public void OnlyNetworkRoutesMovesARoute()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] network = [.. typeof(Network).GetMethods(surface).Select(m => m.Name)];
        string[] route = [.. typeof(ClusterIpv6Route).GetMethods(surface).Select(m => m.Name)];

        Assert.Contains(nameof(Network.MarkObserved), network);
        Assert.DoesNotContain(nameof(Network.ReceivePublicIpv6), network);
        Assert.DoesNotContain(nameof(Network.MarkRequestFailed), network);
        Assert.DoesNotContain(nameof(Network.MarkDeleted), network);
        Assert.Contains(nameof(ClusterIpv6Route.NextToGive), route);
        Assert.DoesNotContain(nameof(ClusterIpv6Route.HandTo), route);
        Assert.DoesNotContain(nameof(ClusterIpv6Route.TakeBack), route);
    }
}
