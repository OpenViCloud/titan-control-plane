using System.Reflection;
using Moq;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/domain-services.md#cross-aggregate-lock: a network still carrying a VM or a load balancer is not
// deleted, and NetworkDeletion is the only way to delete one. Silent about the lock; NetworkDeletionRaceDbTests runs the
// race on Postgres.
public class NetworkDeletionTests
{
    private readonly Mock<INetworkRepository> _networks = new();
    private readonly Tenant _tenant = TestTenant.Active("nd", "kc-nd");

    private NetworkDeletion Deletion() => new(_networks.Object);

    private Network OnIncus()
    {
        Network network = TestAggregates.Network(_tenant, "net-nd", "cluster-1");
        network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    [Fact]
    public async Task ANetworkWithAVm_IsRefused_AndStaysAsItWas()
    {
        Network network = OnIncus();
        _networks.Setup(r => r.HasActiveVmsAsync(network.Id, default)).ReturnsAsync(true);

        BusinessException ex = await Assert.ThrowsAsync<BusinessException>(() => Deletion().DeleteAsync(network));

        Assert.Equal("NETWORK_NOT_EMPTY", ex.Code);
        Assert.False(network.IsDeletedOrDeleting);
    }

    [Fact]
    public async Task ANetworkWithALoadBalancer_IsRefused_AndStaysAsItWas()
    {
        Network network = OnIncus();
        _networks.Setup(r => r.HasLiveLoadBalancersAsync(network.Id, default)).ReturnsAsync(true);

        BusinessException ex = await Assert.ThrowsAsync<BusinessException>(() => Deletion().DeleteAsync(network));

        Assert.Equal("NETWORK_NOT_EMPTY", ex.Code);
        Assert.False(network.IsDeletedOrDeleting);
    }

    [Fact]
    public async Task AnEmptyNetworkOnIncus_HandsOutItsProviderIdAndRequest_AndWaitsForTheDeleteCommand()
    {
        Network network = OnIncus();

        OperationTicket? ticket = await Deletion().DeleteAsync(network);

        Assert.Equal("net-ext", ticket!.ProviderId);
        Assert.Equal((NetworkStatus.Deleting, ticket.OperationId), (network.Status, network.Operation.PendingId!.Value));
        Assert.True(network.IsDeletedOrDeleting);
        Assert.Equal(NetworkStatus.Deleting, network.Status);
    }

    [Fact]
    public async Task AnEmptyNetworkThatNeverReachedIncus_IsDeletedHere_WithNoTicket()
    {
        Network network = TestAggregates.Network(_tenant, "net-nd", "cluster-1");
        network.MarkRequestFailed("create gave up", null);

        OperationTicket? ticket = await Deletion().DeleteAsync(network);

        Assert.Null(ticket);
        Assert.Null(network.Operation.PendingId);
        Assert.True(network.IsDeleted);
    }

    // The compiler keeps Titan.Application off Delete, so NetworkDeletion is the only way to delete a network.
    [Fact]
    public void OnlyNetworkDeletionDeletesANetwork()
    {
        const BindingFlags surface = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] publicMethods = [.. typeof(Network).GetMethods(surface).Select(m => m.Name)];
        Assert.Contains(nameof(Network.Rename), publicMethods);
        Assert.DoesNotContain(nameof(Network.Delete), publicMethods);
    }
}
