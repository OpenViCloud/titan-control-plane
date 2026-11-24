using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Tests.Fakes;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Tests.Services;

// DefaultNetwork.GiveIfNeverHadOneAsync (titan-ops features/network/design.md#default-network): called by both Billing
// activation handlers and the backfill job, never saves itself, and every refusal is quiet — it logs and returns
// false, it never throws, so activation always still succeeds.
public class DefaultNetworkTests
{
    private const string Block56 = "2001:41d0:d00:d400::/56";

    private readonly Mock<INetworkRepository> _networks = new();
    private readonly Mock<IClusterIpv6RouteRepository> _routes = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly FakeActivityRecorder _activity = new();
    private List<ClusterIpv6Route> _routeRows = Enumerable.Range(1, 255).Select(n => ClusterIpv6Route.Create("cluster-1", n)).ToList();

    public DefaultNetworkTests()
    {
        _routes.Setup(r => r.LockFreeAsync("cluster-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _routeRows.Where(route => route.IsFree).ToList());
    }

    private static readonly ClusterConfig WithIpv6 = new()
    {
        Id = "cluster-1", Name = "Cluster 1", Endpoint = "https://localhost:8443", ServerFingerprint = new string('a', 64),
        OvnUplinkNetwork = "ovnup", StoragePool = "default", Ipv6Block = Block56,
    };

    private static readonly ClusterConfig WithoutIpv6 = new()
    {
        Id = "cluster-0", Name = "Cluster 0", Endpoint = "https://localhost:8444", ServerFingerprint = new string('b', 64),
    };

    private DefaultNetwork Service(params ClusterConfig[] configuredClusters) =>
        new(_networks.Object, new NetworkRoutes(_routes.Object), new ClusterOptions { Clusters = [.. configuredClusters] },
            _commands.Object, _activity, NullLogger<DefaultNetwork>.Instance);

    private void NeverHadOne(TenantAggregate tenant, bool everHadOne = false) =>
        _networks.Setup(n => n.HasEverHadOneAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(everHadOne);

    [Fact]
    public async Task ATenantThatNeverHadOne_OnAClusterWithIpv6_GetsADefaultNetwork_Ipv6OnDenyInAllowOut_AndOpensSsh()
    {
        TenantAggregate tenant = TestTenant.WithRoomToSpare();
        NeverHadOne(tenant);
        NetworkAggregate? added = null;
        _networks.Setup(n => n.Add(It.IsAny<NetworkAggregate>())).Callback<NetworkAggregate>(n => added = n);

        bool gave = await Service(WithIpv6).GiveIfNeverHadOneAsync(tenant);

        Assert.True(gave);
        Assert.NotNull(added);
        Assert.Equal("default", added!.Name.Value);
        Assert.True(added.HasPublicIpv6);
        Assert.True(added.OpensSshWhenCreated);
        Assert.Equal((FirewallPolicy.Deny, FirewallPolicy.Allow), (added.DefaultIngressPolicy, added.DefaultEgressPolicy));
        Assert.Equal(NetworkStatus.Creating, added.Status);
        _commands.Verify(c => c.EnqueueNetworkCreateAsync(added, added.Operation.PendingId!.Value,
            TenantAggregate.ProjectNameFor(tenant.Id), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        var staged = Assert.Single(_activity.Events);
        Assert.Equal(("network.create_requested", tenant.Id, added.Id), (staged.Action, staged.OutcomeTenantId, staged.ResourceId));
    }

    [Fact]
    public async Task ATenantThatAlreadyHadOne_GetsNothing_AndNothingIsTouched()
    {
        TenantAggregate tenant = TestTenant.WithRoomToSpare();
        NeverHadOne(tenant, everHadOne: true);

        bool gave = await Service(WithIpv6).GiveIfNeverHadOneAsync(tenant);

        Assert.False(gave);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
        _commands.Verify(c => c.EnqueueNetworkCreateAsync(It.IsAny<NetworkAggregate>(), It.IsAny<Guid>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task NoClusterHasPublicIpv6_GetsNothing()
    {
        TenantAggregate tenant = TestTenant.WithRoomToSpare();
        NeverHadOne(tenant);

        bool gave = await Service(WithoutIpv6).GiveIfNeverHadOneAsync(tenant);

        Assert.False(gave);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
    }

    [Fact]
    public async Task TheFirstClusterConfigured_WithoutIpv6_IsSkipped_ForTheNextOneThatHasIt()
    {
        TenantAggregate tenant = TestTenant.WithRoomToSpare();
        NeverHadOne(tenant);
        NetworkAggregate? added = null;
        _networks.Setup(n => n.Add(It.IsAny<NetworkAggregate>())).Callback<NetworkAggregate>(n => added = n);

        bool gave = await Service(WithoutIpv6, WithIpv6).GiveIfNeverHadOneAsync(tenant);

        Assert.True(gave);
        Assert.Equal("cluster-1", added!.ClusterId);
    }

    [Fact]
    public async Task NoRoomForANetwork_GetsNothing()
    {
        TenantAggregate tenant = TenantAggregate.CreatePending("acme", "owner-1", maxNetworks: 0);
        tenant.Approve();
        NeverHadOne(tenant);

        bool gave = await Service(WithIpv6).GiveIfNeverHadOneAsync(tenant);

        Assert.False(gave);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
    }

    [Fact]
    public async Task NoRoomForPublicIpv6_GetsNothing()
    {
        TenantAggregate tenant = TestTenant.WithRoomToSpare();
        tenant.UpdateQuota(TenantQuota.Create(
            maxVms: tenant.Quota.MaxVms, maxNetworks: tenant.Quota.MaxNetworks, maxVolumes: tenant.Quota.MaxVolumes,
            maxVmSnapshotsPerVm: tenant.Quota.MaxVmSnapshotsPerVm,
            maxVolumeSnapshotsPerVolume: tenant.Quota.MaxVolumeSnapshotsPerVolume,
            maxFirewallRulesPerNetwork: tenant.Quota.MaxFirewallRulesPerNetwork, maxPublicIpv6: 0,
            maxSshKeys: tenant.Quota.MaxSshKeys, maxLoadBalancers: tenant.Quota.MaxLoadBalancers));
        NeverHadOne(tenant);

        bool gave = await Service(WithIpv6).GiveIfNeverHadOneAsync(tenant);

        Assert.False(gave);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
    }

    [Fact]
    public async Task EveryRouteOfTheClusterIsHeld_GetsNothing()
    {
        TenantAggregate tenant = TestTenant.WithRoomToSpare();
        NeverHadOne(tenant);
        foreach (ClusterIpv6Route route in _routeRows)
        {
            route.HandTo(Guid.NewGuid());
        }

        bool gave = await Service(WithIpv6).GiveIfNeverHadOneAsync(tenant);

        Assert.False(gave);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
    }
}
