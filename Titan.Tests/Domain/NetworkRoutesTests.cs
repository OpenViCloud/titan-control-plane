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

    private static NetworkOperationEnd CreateFailed(string? externalId = null) =>
        NetworkOperationEnd.Failed(externalId, null, "Network creation failed.", "raw");

    [Fact]
    public async Task CreateFailedBeforeIncusHadIt_LetsGoOfTheSubnet_AndTheRouteGoesBack()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        ClusterIpv6Route route = RouteOf(network);

        NetworkOperationEnded? ended = await Routes().RecordEndedAsync(network, network.Pending!.Id, CreateFailed());

        Assert.True(ended!.LetGoOfPublicIpv6);
        Assert.False(network.HasPublicIpv6);
        Assert.Equal("Network creation failed.", network.OperationFailureReason);
        Assert.True(route.IsFree);
        Assert.NotNull(route.ReleasedAt);
    }

    // Incus made the network, router and all, before the command gave up: the /64 and its route stay until the delete.
    [Fact]
    public async Task CreateFailedAfterIncusHadIt_KeepsTheSubnet_AndTheRoute()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);

        NetworkOperationEnded? ended = await Routes().RecordEndedAsync(network, network.Pending!.Id, CreateFailed("net-x"));

        Assert.False(ended!.LetGoOfPublicIpv6);
        Assert.True(network.HasPublicIpv6);
        Assert.False(RouteOf(network).IsFree);
    }

    [Fact]
    public async Task CreateDone_KeepsTheSubnet_AndTheRoute()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);

        await Routes().RecordEndedAsync(network, network.Pending!.Id,
            NetworkOperationEnd.Done("net-x", new NetworkObservation(NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1")));

        Assert.True(network.HasPublicIpv6);
        Assert.False(RouteOf(network).IsFree);
    }

    // Only the waiting request's own report ends it, so a stale give-up cannot take back a route a live network holds.
    [Fact]
    public async Task AReportForAnotherRequest_EndsNothing_AndKeepsTheRoute()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);

        NetworkOperationEnded? ended = await Routes().RecordEndedAsync(network, Guid.NewGuid(), CreateFailed());

        Assert.Null(ended);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
        Assert.True(network.HasPublicIpv6);
        Assert.False(RouteOf(network).IsFree);
    }

    [Fact]
    public async Task DeleteDone_TakesTheRouteBack_Once()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        ClusterIpv6Route route = RouteOf(network);
        network.MarkActive("net-x", "10.0.0.0/24", "10.0.0.1");
        Guid deleteId = network.Delete()!.OperationId;

        NetworkOperationEnded? first = await Routes().RecordEndedAsync(network, deleteId, NetworkOperationEnd.Done(null, null));
        DateTimeOffset? releasedAt = route.ReleasedAt;
        NetworkOperationEnded? second = await Routes().RecordEndedAsync(network, deleteId, NetworkOperationEnd.Done(null, null));

        Assert.True(network.IsTerminal);
        Assert.True(route.IsFree);
        Assert.NotNull(releasedAt);
        Assert.Equal(releasedAt, route.ReleasedAt);
        Assert.Equal(NetworkOperationKind.Delete, first!.Operation.Kind);
        Assert.Null(second);
    }

    [Fact]
    public async Task DeleteFailed_KeepsTheRoute()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        network.MarkActive("net-x", "10.0.0.0/24", "10.0.0.1");
        Guid deleteId = network.Delete()!.OperationId;

        await Routes().RecordEndedAsync(network, deleteId, NetworkOperationEnd.Failed(null, null, "Network deletion failed.", "raw"));

        Assert.False(network.IsTerminal);
        Assert.True(network.HasPublicIpv6);
        Assert.False(RouteOf(network).IsFree);
    }

    [Fact]
    public async Task DeleteDone_OfANetworkWithoutPublicIpv6_TouchesNoRoute()
    {
        Network network = WithoutPublicIpv6();
        network.MarkActive("net-x", "10.0.0.0/24", "10.0.0.1");
        Guid deleteId = network.Delete()!.OperationId;

        await Routes().RecordEndedAsync(network, deleteId, NetworkOperationEnd.Done(null, null));

        Assert.True(network.IsTerminal);
        Assert.All(_rows, route => Assert.True(route.IsFree));
    }

    // A network holding a /64 with no provider id is one whose create still waits: an observation ends no create, and
    // a create that fails before Incus lets go of the /64. A local delete then would keep the route, since no delete
    // report follows.
    [Fact]
    public async Task RequestDelete_OfANetworkStillHoldingItsSubnet_WithNoId_IsRefused()
    {
        Network network = WantingPublicIpv6();
        await Routes().GiveAsync(network, Block56);
        network.MarkNotFound();

        BusinessException ex = Assert.Throws<BusinessException>(() => network.Delete());

        Assert.Equal("NETWORK_INVALID_STATE", ex.Code);
        Assert.False(network.IsTerminal);
        Assert.False(RouteOf(network).IsFree);
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
        // EndOperation lets go of the /64 on a create that failed before Incus, and a delete it ends gives the route back.
        Assert.DoesNotContain(nameof(Network.EndOperation), network);
        Assert.Contains(nameof(ClusterIpv6Route.NextToGive), route);
        Assert.DoesNotContain(nameof(ClusterIpv6Route.HandTo), route);
        Assert.DoesNotContain(nameof(ClusterIpv6Route.TakeBack), route);
    }
}
