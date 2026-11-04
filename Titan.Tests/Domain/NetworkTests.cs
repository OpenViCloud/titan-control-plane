using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

public class NetworkTests
{
    // ── Provider id is handed out, never asserted by the caller (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id) ──

    [Fact]
    public void RequestDelete_YieldsTheProviderId()
    {
        var network = MakeActiveNetwork();
        Assert.Equal("cs-net-id", network.RequestDelete().ProviderId);
    }

    [Fact]
    public void ProviderIdForVm_WhenCreated_YieldsTheProviderId_AndMutatesNothing()
    {
        var network = MakeActiveNetwork();
        Assert.Equal("cs-net-id", network.ProviderIdForVm("cluster-1"));
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
    }

    // DeleteRequested: Incus still shows the network Created, and a VM must still not land on it.
    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.Errored)]
    [InlineData(Seed.Pending)]
    [InlineData(Seed.NotFound)]
    public void ProviderIdForVm_WhenNotUsable_Throws(Seed seed)
    {
        var network = NetworkIn(seed);
        Assert.Equal("NETWORK_NOT_ACTIVE", Assert.Throws<BusinessException>(() => network.ProviderIdForVm("cluster-1")).Code);
    }

    // A VM lands on its network's cluster (titan-ops features/catalog/design.md#cluster-is-a-real-choice).
    [Fact]
    public void ProviderIdForVm_ForAVmOfAnotherCluster_IsRefused()
    {
        var network = MakeActiveNetwork();
        var ex = Assert.Throws<BusinessException>(() => network.ProviderIdForVm("other-cluster"));
        Assert.Equal("NETWORK_CLUSTER_MISMATCH", ex.Code);
    }

    [Fact]
    public void ProviderIdForVm_AnotherCluster_IsAnsweredBeforeTheState()
    {
        var network = NetworkIn(Seed.Creating);
        var ex = Assert.Throws<BusinessException>(() => network.ProviderIdForVm("other-cluster"));
        Assert.Equal("NETWORK_CLUSTER_MISMATCH", ex.Code);
    }

    [Fact]
    public void AddFirewallRule_WhenCreated_YieldsTheProviderId()
    {
        Assert.Equal("cs-net-id", AddARule(MakeActiveNetwork()).ExternalNetworkId);
    }

    private static FirewallRuleTicket AddARule(Network network) =>
        network.AddFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, PortRange.Of(80, 80), FirewallCidr.Parse("0.0.0.0/0"));

    // A DIFFERENT code from ProviderIdForVm, on purpose: the firewall endpoint answers FIREWALL_NETWORK_NOT_READY.
    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.Errored)]
    [InlineData(Seed.NotFound)]
    public void AddFirewallRule_WhenNotUsable_Throws(Seed seed)
    {
        var network = NetworkIn(seed);
        Assert.Equal("FIREWALL_NETWORK_NOT_READY", Assert.Throws<BusinessException>(() => AddARule(network)).Code);
    }

    public enum Seed { Creating, Active, DeleteRequested, Deleted, CreateFailed, Errored, Pending, NotFound }

    private static Network NetworkIn(Seed seed)
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "my-net", "cluster-1");
        switch (seed)
        {
            case Seed.Creating: break;
            case Seed.Active: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); break;
            case Seed.DeleteRequested: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.RequestDelete(); break;
            case Seed.Deleted: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.RequestDelete(); network.MarkDeleted(); break;
            case Seed.CreateFailed: network.MarkRequestFailed("reason", "detail"); break;
            case Seed.Errored: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.MarkObserved(NetworkStatus.Errored); break;
            case Seed.Pending: network.MarkCreated("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.MarkObserved(NetworkStatus.Pending); break;
            case Seed.NotFound: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.MarkNotFound(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed), seed, "unmapped seed");
        }
        return network;
    }

    // ── Create: nothing on Incus yet, a create waiting ────

    [Fact]
    public void Create_HasNoIncusStatus_IsNotCreated_AndWaitsForItsCreate()
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "my-net", "cluster-1");

        Assert.Null(network.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, network.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
    }

    // The report's cidr and gateway are optional (omitempty; today the worker leaves them out only on a give-up that
    // could not read the range back); an empty string means the same. Neither is stored.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MarkCreated_WithoutAnAddress_LeavesCidrAndGatewayNull(string? absent)
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "my-net", "cluster-1");

        network.MarkCreated("cs-net-id", absent, absent);

        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Null(network.Cidr);
        Assert.Null(network.Gateway);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
    }

    // Every word Incus reports is recorded as it is (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    [Theory]
    [InlineData(NetworkStatus.Pending)]
    [InlineData(NetworkStatus.Created)]
    [InlineData(NetworkStatus.Errored)]
    [InlineData(NetworkStatus.Unknown)]
    [InlineData(NetworkStatus.Unavailable)]
    public void MarkObserved_RecordsTheIncusWord_AndThatIncusHasIt(NetworkStatus status)
    {
        var network = MakeActiveNetwork();

        network.MarkObserved(status);

        Assert.Equal(status, network.CurrentState);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal("10.0.0.0/24", network.Cidr);
    }

    [Theory]
    [InlineData(NetworkStatus.Errored)]
    [InlineData(NetworkStatus.Unavailable)]
    [InlineData(NetworkStatus.Created)]
    public void NoObservation_WithdrawsAWaitingDelete(NetworkStatus status)
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();

        network.MarkObserved(status);

        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
    }

    // ── Not found: the reconciler never reports Deleted (titan-ops architecture/desired-and-current.md#existence-axis) ──

    [Fact]
    public void NotFound_ClearsTheStatus_KeepsTheIdAndAddress()
    {
        var network = MakeActiveNetwork();

        network.MarkNotFound();

        Assert.Equal(CurrentExistence.NotFound, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
    }

    [Fact]
    public void NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();

        network.MarkNotFound();

        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        Assert.False(network.IsTerminal);
        Assert.NotNull(network.MarkDeleted());
    }

    [Fact]
    public void SeenAgainAfterNotFound_Exists()
    {
        var network = MakeActiveNetwork();
        network.MarkNotFound();

        network.MarkObserved(NetworkStatus.Created);

        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    // ── Request failure is a field, not a state (titan-ops architecture/desired-and-current.md#request-failure) ──

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsTheFailure_EndsTheCreate_AndLeavesItNotCreated()
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "my-net", "cluster-1");

        network.MarkRequestFailed("Network creation failed.", "raw incus error");

        Assert.Equal("Network creation failed.", network.RequestFailureReason);
        Assert.Equal("raw incus error", network.RequestFailureDetail);
        Assert.NotNull(network.RequestFailedAt);
        Assert.Null(network.Transition);
        Assert.Equal(CurrentExistence.NotCreated, network.CurrentExistence);
        Assert.Null(network.CurrentState);
    }

    [Fact]
    public void RequestFailed_WithNoCreateWaiting_IsStale_AndRecordsNothing()
    {
        var network = MakeActiveNetwork();

        network.MarkRequestFailed("late", "late");

        Assert.Null(network.RequestFailureReason);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    // ── RequestDelete: follows Incus — whatever its word, a network Incus may hold is deleted through Incus ──

    [Fact]
    public void RequestDelete_WritesDesiredExistence_AndLeavesTheObservationAlone()
    {
        var network = MakeActiveNetwork();

        network.RequestDelete();

        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Theory]
    [InlineData(NetworkStatus.Pending)]
    [InlineData(NetworkStatus.Errored)]
    [InlineData(NetworkStatus.Unknown)]
    [InlineData(NetworkStatus.Unavailable)]
    public void RequestDelete_WithAProviderId_IsAccepted_WhateverIncusSays(NetworkStatus status)
    {
        var network = MakeActiveNetwork();
        network.MarkObserved(status);

        Assert.Equal("cs-net-id", network.RequestDelete().ProviderId);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void RequestDelete_OfANetworkIncusNoLongerHas_GoesThroughIncus()
    {
        var network = NetworkIn(Seed.NotFound);

        Assert.Equal("cs-net-id", network.RequestDelete().ProviderId);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void RequestDelete_WhileTheCreateWaits_Throws()
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "my-net", "cluster-1");

        var ex = Assert.Throws<BusinessException>(() => network.RequestDelete());

        Assert.Equal("NETWORK_INVALID_STATE", ex.Code);
    }

    [Fact]
    public void RequestDelete_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var network = MakeActiveNetwork();
        Guid first = network.RequestDelete().RequestId;

        var ex = Assert.Throws<BusinessException>(() => network.RequestDelete());

        Assert.Equal("NETWORK_INVALID_STATE", ex.Code);
        Assert.Equal((NetworkRequestKind.Delete, first), (network.Pending!.Kind, network.Pending.Id));
    }

    [Fact]
    public void RequestDelete_WhenDeleted_Throws()
    {
        var network = NetworkIn(Seed.Deleted);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => network.RequestDelete()).Code);
    }

    [Fact]
    public void RequestDelete_OfAFailedCreateWithNoId_Throws_ItIsDeletedLocally()
    {
        var network = NetworkIn(Seed.CreateFailed);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => network.RequestDelete()).Code);
    }

    // ── Delete outcome ─────────────────────────────────────

    [Fact]
    public void MarkDeleted_EndsTheWaitingDelete_Once_AndClearsTheStatus()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();

        NetworkRequestEnded? first = network.MarkDeleted();
        NetworkRequestEnded? repeat = network.MarkDeleted();

        Assert.Equal(NetworkRequestKind.Delete, first!.Request.Kind);
        Assert.Null(repeat);
        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.True(network.IsTerminal);
        Assert.Null(network.Transition);
    }

    // ── Terminal state absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs) ──

    [Fact]
    public void Deleted_IsAbsorbing_ForEveryLaterObservation()
    {
        var network = NetworkIn(Seed.Deleted);

        network.MarkObserved(NetworkStatus.Created);
        network.MarkNotFound();
        network.MarkCreated("other", "10.9.0.0/24", "10.9.0.1");
        network.MarkRequestFailed("late", "late");

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Null(network.RequestFailureReason);
    }

    // ── Rename ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    public void Rename_WhenBeingOrBeenDeleted_Throws(Seed seed)
    {
        var network = NetworkIn(seed);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => network.Rename(NetworkName.Parse("new"))).Code);
    }

    // ── Create: the range asked for against the public IPv6 choice (titan-ops architecture/code-structure.md#logic-placement) ──

    [Theory]
    [InlineData("8.8.8.0/24")]
    [InlineData("172.32.0.0/24")]
    [InlineData("9.0.0.0/7")]
    public void Create_WithPublicIpv6_AndACidrOutsideThePrivateRanges_IsRefused(string cidr)
    {
        var ex = Assert.Throws<BusinessException>(() => Network.Create(
            TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "singapore01", Ipv4Cidr.Parse(cidr), true));

        Assert.Equal("NETWORK_CIDR_NOT_PRIVATE", ex.Code);
    }

    [Theory]
    [InlineData("10.20.0.0/24")]
    [InlineData("172.16.5.0/24")]
    [InlineData("192.168.10.0/24")]
    public void Create_WithPublicIpv6_AndAPrivateCidr_IsAccepted(string cidr)
    {
        var network = Network.Create(
            TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "singapore01", Ipv4Cidr.Parse(cidr), true);

        Assert.Equal("net-1", network.Name.Value);
        Assert.Equal(Ipv4Cidr.Parse(cidr), network.RequestedCidr);
    }

    [Fact]
    public void Create_WithPublicIpv6_AndNoCidr_IsAccepted()
    {
        Assert.NotNull(Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "singapore01", null, true));
    }

    // A network without public IPv6 sits on no uplink, so any IPv4 range is its own business.
    [Fact]
    public void Create_WithoutPublicIpv6_TakesAnyIpv4Cidr()
    {
        var network = Network.Create(
            TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "mock", Ipv4Cidr.Parse("8.8.8.0/24"), false);

        Assert.False(network.HasPublicIpv6);
    }

    // ── DeleteLocally ──────────────────────────────────────────────────────

    [Fact]
    public void DeleteLocally_OnAFailedCreateWithNoId_EndsDeleted()
    {
        var network = NetworkIn(Seed.CreateFailed);

        network.DeleteLocally();

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Null(network.Transition);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Active)]
    [InlineData(Seed.NotFound)]
    public void DeleteLocally_OnANetworkIncusMayHold_OrWhileItsCreateWaits_IsRefused(Seed seed)
    {
        var network = NetworkIn(seed);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(network.DeleteLocally).Code);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Network MakeActiveNetwork()
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "my-net", "cluster-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    // ---- What the customer asked for is the network's own field (titan-ops architecture/code-structure.md#logic-placement).

    [Fact]
    public void Create_HoldsTheRequestedCidr_FromTheMomentItIsAskedFor()
    {
        Network network = Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "singapore01",
            Ipv4Cidr.Parse("10.1.0.0/24"), false);

        Assert.Equal(Ipv4Cidr.Parse("10.1.0.0/24"), network.RequestedCidr);
        Assert.Null(network.Cidr); // Incus has not answered yet
    }

    [Fact]
    public void Create_WithoutACidr_HoldsNoRequestedCidr()
    {
        Network network = Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "singapore01", null, false);

        Assert.Null(network.RequestedCidr);
    }
}
