using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Billing.TenantActivation;
using Titan.Application.Commands.Billing.TenantActivation.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Tests.Fakes;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Tests.Services;

// titan-ops features/network/design.md#default-network: both of Billing's activation handlers call
// DefaultNetwork after the tenant is Active, before their one save — never on a repeat that changes nothing.
public class TenantActivationDefaultNetworkTests
{
    private const string Block56 = "2001:41d0:d00:d400::/56";

    private readonly Mock<ITenantRepository> _tenantRepo = new();
    private readonly Mock<IWalletRepository> _walletRepo = new();
    private readonly Mock<INetworkRepository> _networks = new();
    private readonly Mock<IClusterIpv6RouteRepository> _routes = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();
    private readonly FakeActivityRecorder _activity = new();

    public TenantActivationDefaultNetworkTests()
    {
        _tenantRepo.Setup(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid id, CancellationToken ct) => _tenantRepo.Object.FindByIdAsync(id, ct));
        _routes.Setup(r => r.LockFreeAsync("cluster-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Range(1, 255).Select(n => ClusterIpv6Route.Create("cluster-1", n)).ToList());
    }

    private static readonly ClusterOptions WithIpv6 = new()
    {
        Clusters =
        [
            new()
            {
                Id = "cluster-1", Name = "Cluster 1", Endpoint = "https://localhost:8443", ServerFingerprint = new string('a', 64),
                OvnUplinkNetwork = "ovnup", StoragePool = "default", Ipv6Block = Block56,
            },
        ],
    };

    private DefaultNetwork Service(ClusterOptions clusters) => new(
        _networks.Object, new NetworkRoutes(_routes.Object), clusters, _commands.Object, _activity, NullLogger<DefaultNetwork>.Instance);

    private void NeverHadOne(Guid tenantId) =>
        _networks.Setup(n => n.HasEverHadOneAsync(tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(false);

    private void Known(Tenant tenant) =>
        _tenantRepo.Setup(r => r.FindByIdAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);

    [Fact]
    public async Task Approve_APendingTenant_WithAnIpv6Cluster_GivesADefaultNetwork_BeforeTheWalletsSave()
    {
        Tenant tenant = Tenant.CreatePending("acme", "owner-1");
        Known(tenant);
        NeverHadOne(tenant.Id);
        var saveOrder = new List<string>();
        _networks.Setup(n => n.Add(It.IsAny<NetworkAggregate>())).Callback(() => saveOrder.Add("network.add"));
        _walletRepo.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => saveOrder.Add("wallet.save")).Returns(Task.CompletedTask);
        var handler = new ApproveDeclaredAccountCommandHandler(_tenantRepo.Object, _walletRepo.Object,
            new TenantActivation(_walletRepo.Object), Service(WithIpv6));

        await handler.HandleAsync(new ApproveDeclaredAccountCommand(tenant.Id));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Once);
        Assert.Equal(["network.add", "wallet.save"], saveOrder);
    }

    [Fact]
    public async Task Approve_ATenantNoLongerPending_IsARepeat_AndNeverAsksForADefaultNetwork()
    {
        Tenant tenant = Tenant.CreatePending("acme", "owner-1");
        tenant.Approve();
        Known(tenant);
        var handler = new ApproveDeclaredAccountCommandHandler(_tenantRepo.Object, _walletRepo.Object,
            new TenantActivation(_walletRepo.Object), Service(WithIpv6));

        await handler.HandleAsync(new ApproveDeclaredAccountCommand(tenant.Id));

        _networks.Verify(n => n.HasEverHadOneAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
    }

    [Fact]
    public async Task Activate_APendingTenant_WithAnIpv6Cluster_GivesADefaultNetwork_BeforeTheWalletsSave()
    {
        Tenant tenant = Tenant.CreatePending("acme", "owner-1");
        Known(tenant);
        NeverHadOne(tenant.Id);
        var saveOrder = new List<string>();
        _networks.Setup(n => n.Add(It.IsAny<NetworkAggregate>())).Callback(() => saveOrder.Add("network.add"));
        _walletRepo.Setup(w => w.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => saveOrder.Add("wallet.save")).Returns(Task.CompletedTask);
        var handler = new ActivateTenantCommandHandler(_tenantRepo.Object, _walletRepo.Object,
            new TenantActivation(_walletRepo.Object), Service(WithIpv6));

        await handler.HandleAsync(new ActivateTenantCommand(tenant.Id));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Once);
        Assert.Equal(["network.add", "wallet.save"], saveOrder);
    }

    [Fact]
    public async Task Activate_ATenantAlreadyActive_IsARepeat_AndNeverAsksForADefaultNetwork()
    {
        Tenant tenant = Tenant.CreatePending("acme", "owner-1");
        tenant.Approve();
        Known(tenant);
        var handler = new ActivateTenantCommandHandler(_tenantRepo.Object, _walletRepo.Object,
            new TenantActivation(_walletRepo.Object), Service(WithIpv6));

        await handler.HandleAsync(new ActivateTenantCommand(tenant.Id));

        _networks.Verify(n => n.HasEverHadOneAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
    }

    // Suspended → Active again is not a first activation; the tenant already has a row in core.networks.
    [Fact]
    public async Task Activate_ASuspendedTenantThatAlreadyHadANetwork_GetsNoSecondOne()
    {
        Tenant tenant = Tenant.CreatePending("acme", "owner-1");
        tenant.Approve();
        tenant.Suspend();
        Known(tenant);
        _networks.Setup(n => n.HasEverHadOneAsync(tenant.Id, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var handler = new ActivateTenantCommandHandler(_tenantRepo.Object, _walletRepo.Object,
            new TenantActivation(_walletRepo.Object), Service(WithIpv6));

        await handler.HandleAsync(new ActivateTenantCommand(tenant.Id));

        Assert.Equal(TenantStatus.Active, tenant.Status);
        _networks.Verify(n => n.Add(It.IsAny<NetworkAggregate>()), Times.Never);
    }
}
