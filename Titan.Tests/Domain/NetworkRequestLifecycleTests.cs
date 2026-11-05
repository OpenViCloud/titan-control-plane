using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#invariants for a network and its firewall rules: one waiting request each
// (the rule's independent of the network's), ended only by the report carrying its id; an observation writes current
// and ends nothing; a failure puts desired back according to the kind (titan-ops architecture/resource-lifecycle.md#kinds).
// NetworkTests keeps what a request may do from each situation; this file is about how a request ends.
public class NetworkRequestLifecycleTests
{
    private const string Subnet = "2001:41d0:d00:d405::/64";

    private static Network NewNetwork() => TestAggregates.Network(TestTenant.WithRoomToSpare(), "net-1", "cluster-1");

    private static Network WantingPublicIpv6()
    {
        Network network = Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse("net-1"), "cluster-1", null,
            wantsPublicIpv6: true);
        network.ReceivePublicIpv6(Subnet);
        return network;
    }

    private static NetworkObservation Seen(NetworkStatus? status) => new(status, "10.0.0.0/24", "10.0.0.1");

    private static Network UsableNetwork()
    {
        Network network = NewNetwork();
        network.FinishOperation(network.Pending!.Id, "net-ext", Seen(NetworkStatus.Created));
        return network;
    }

    private static PendingOperation<NetworkOperationKind>? Failed(Network network, Guid operationId,
        string? externalId = null) =>
        network.FailOperation(operationId, externalId, null, "The network request could not be completed.", "raw");

    private static FirewallRule NewRule(Network network, int port) =>
        network.AddFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, PortRange.Of(port, port),
            FirewallCidr.Parse("0.0.0.0/0")).Rule;

    private static FirewallRule ActiveRule(Network network, int port)
    {
        FirewallRule rule = NewRule(network, port);
        network.FinishFirewallRuleOperation(rule.Id, rule.Pending!.Id, $"acl-net/{port}", FirewallRuleState.enabled);
        return rule;
    }

    private static PendingOperation<FirewallRuleOperationKind>? RuleFailed(Network network, Guid ruleId, Guid operationId,
        string? externalId = null) =>
        network.FailFirewallRuleOperation(ruleId, operationId, externalId, null,
            "The firewall rule request could not be completed.", "raw");

    // ---------- the network's create ----------

    [Fact]
    public void ANewNetwork_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        Network network = NewNetwork();

        Assert.Equal(NetworkOperationKind.Create, network.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, network.Pending.Id);
        Assert.True(network.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdExistenceAddressAndStatus_AndEndsIt()
    {
        Network network = NewNetwork();
        Guid createId = network.Pending!.Id;

        PendingOperation<NetworkOperationKind>? ended = network.FinishOperation(createId, "net-ext", Seen(NetworkStatus.Created));

        Assert.Equal((NetworkOperationKind.Create, createId), (ended!.Kind, ended.Id));
        Assert.Null(network.Pending);
        Assert.Null(network.Transition);
        Assert.Equal(("net-ext", CurrentExistence.Exists, NetworkStatus.Created),
            (network.ExternalId, network.CurrentExistence, network.CurrentState));
        Assert.Equal(("10.0.0.0/24", "10.0.0.1"), (network.Cidr, network.Gateway));
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("net-ext", network.ProviderIdForVm("cluster-1"));
    }

    // A worker from before the request routes reports the create's Done with no status (the old Created): the network
    // exists, and is usable only once the reconciler reads it Created.
    [Fact]
    public void Done_OfTheCreate_WithoutAStatus_LeavesTheStateNull_AndTheNetworkNotUsable_UntilTheReconcilerReadsCreated()
    {
        Network network = NewNetwork();

        network.FinishOperation(network.Pending!.Id, "net-ext", Seen(null));

        Assert.Null(network.Pending);
        Assert.Null(network.CurrentState);
        Assert.Equal(("net-ext", CurrentExistence.Exists), (network.ExternalId, network.CurrentExistence));
        Assert.Equal("FIREWALL_NETWORK_NOT_READY", Assert.Throws<BusinessException>(() => NewRule(network, 22)).Code);
        Assert.Equal("NETWORK_NOT_ACTIVE",
            Assert.Throws<BusinessException>(() => network.ProviderIdForVm("cluster-1")).Code);

        network.MarkObserved(NetworkStatus.Created);

        Assert.Equal("net-ext", network.ProviderIdForVm("cluster-1"));
        Assert.Equal("net-ext", network.AddFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP,
            PortRange.Of(22, 22), FirewallCidr.Parse("0.0.0.0/0")).ExternalNetworkId);
    }

    // ---------- observations end nothing ----------

    [Theory]
    [InlineData(NetworkStatus.Created)]
    [InlineData(NetworkStatus.Errored)]
    [InlineData(NetworkStatus.Unavailable)]
    [InlineData(NetworkStatus.Pending)]
    [InlineData(NetworkStatus.Unknown)]
    public void AnObservation_WritesCurrentOnly_AndLeavesAWaitingCreateWaiting(NetworkStatus status)
    {
        Network network = NewNetwork();
        Guid createId = network.Pending!.Id;

        network.MarkObserved(status);

        Assert.Equal((NetworkOperationKind.Create, createId), (network.Pending!.Kind, network.Pending.Id));
        Assert.Equal(NetworkTransition.Creating, network.Transition);
        Assert.Equal((CurrentExistence.Exists, (NetworkStatus?)status), (network.CurrentExistence, network.CurrentState));
        Assert.Null(network.ExternalId);
        Assert.Null(network.OperationFailureReason);
        // A network still waiting on its create is not usable, even read Created.
        Assert.Equal("FIREWALL_NETWORK_NOT_READY", Assert.Throws<BusinessException>(() => NewRule(network, 22)).Code);
    }

    [Fact]
    public void NotFound_WritesCurrentOnly_AndLeavesAWaitingCreateWaiting()
    {
        Network network = NewNetwork();
        Guid createId = network.Pending!.Id;

        network.MarkNotFound();

        Assert.Equal(createId, network.Pending!.Id);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
        Assert.Equal(CurrentExistence.NotFound, network.CurrentExistence);
        Assert.Null(network.OperationFailureReason);
    }

    // ---------- only the report with the id ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsNull()
    {
        Network network = NewNetwork();
        Guid createId = network.Pending!.Id;

        PendingOperation<NetworkOperationKind>? ended = network.FinishOperation(Guid.NewGuid(), "net-ext", Seen(NetworkStatus.Created));

        Assert.Null(ended);
        Assert.Equal(createId, network.Pending!.Id);
        Assert.Null(network.ExternalId);
        Assert.Null(network.Cidr);
        Assert.Null(network.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, network.CurrentExistence);
    }

    [Fact]
    public void AReport_WithNothingWaiting_ChangesNothing_AndReturnsNull()
    {
        Network network = NewNetwork();
        Guid createId = network.Pending!.Id;
        network.FinishOperation(createId, "net-ext", Seen(NetworkStatus.Created));

        Assert.Null(Failed(network, createId, "other"));
        Assert.Null(Failed(network, Guid.NewGuid()));

        Assert.Equal("net-ext", network.ExternalId);
        Assert.Null(network.OperationFailureReason);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    [Fact]
    public void AReport_AfterDeleted_IsIgnored()
    {
        Network network = UsableNetwork();
        Guid deleteId = network.Delete()!.OperationId;
        network.FinishOperation(deleteId, null, null);

        Assert.Null(Failed(network, deleteId));
        Assert.Null(network.FinishOperation(Guid.NewGuid(), "other", Seen(NetworkStatus.Created)));
        network.MarkObserved(NetworkStatus.Created);
        network.MarkNotFound();

        Assert.Equal((CurrentExistence.Deleted, (NetworkStatus?)null), (network.CurrentExistence, network.CurrentState));
        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Equal("net-ext", network.ExternalId);
        Assert.Null(network.OperationFailureReason);
        Assert.Null(network.Pending);
    }

    // ---------- the network's delete ----------

    [Fact]
    public void RequestDelete_EveryTime_GivesAFreshRequestId_TheOneTheNetworkWaitsOn()
    {
        Network network = UsableNetwork();

        OperationTicket first = network.Delete()!;
        Assert.Equal((NetworkOperationKind.Delete, first.OperationId), (network.Pending!.Kind, network.Pending.Id));
        Failed(network, first.OperationId);
        OperationTicket second = network.Delete()!;

        Assert.NotEqual(Guid.Empty, first.OperationId);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(second.OperationId, network.Pending!.Id);
        Assert.Equal(("net-ext", "net-ext"), (first.ProviderId, second.ProviderId));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheNetworkBack_WithTheReason_AndTheNextDeleteIsANewRequest()
    {
        Network network = UsableNetwork();
        Guid firstDelete = network.Delete()!.OperationId;

        PendingOperation<NetworkOperationKind>? ended = Failed(network, firstDelete);

        Assert.Equal(NetworkOperationKind.Delete, ended!.Kind);
        Assert.Null(network.Pending);
        Assert.Null(network.Transition);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal((CurrentExistence.Exists, (NetworkStatus?)NetworkStatus.Created), (network.CurrentExistence, network.CurrentState));
        Assert.Equal(("The network request could not be completed.", "raw"),
            (network.OperationFailureReason, network.OperationFailureDetail));
        Assert.NotNull(network.OperationFailedAt);

        Guid secondDelete = network.Delete()!.OperationId;

        Assert.NotEqual(firstDelete, secondDelete);
        Assert.Null(Failed(network, firstDelete)); // the first delete's late report is stale
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void Done_AfterAnOlderFailure_ClearsIt()
    {
        Network network = UsableNetwork();
        Failed(network, network.Delete()!.OperationId);

        network.FinishOperation(network.Delete()!.OperationId, null, null);

        Assert.Null(network.OperationFailureReason);
        Assert.Null(network.OperationFailureDetail);
        Assert.Null(network.OperationFailedAt);
    }

    // The delete removed the network's ACL and every rule in it (titan-ops features/network/design.md#network-lifecycle).
    [Fact]
    public void Done_OfTheDelete_EndsTheNetworkAndEveryRule_NamingOnlyTheRulesWhoseOwnDeleteWaited()
    {
        Network network = UsableNetwork();
        FirewallRule creating = NewRule(network, 21);
        FirewallRule active = ActiveRule(network, 22);
        FirewallRule deleting = ActiveRule(network, 23);
        network.DeleteFirewallRule(deleting.Id);
        FirewallRule failedCreate = NewRule(network, 24);
        RuleFailed(network, failedCreate.Id, failedCreate.Pending!.Id);
        FirewallRule alreadyGone = ActiveRule(network, 25);
        network.FinishFirewallRuleOperation(alreadyGone.Id, network.DeleteFirewallRule(alreadyGone.Id)!.OperationId, null, null);
        Guid deleteId = network.Delete()!.OperationId;

        PendingOperation<NetworkOperationKind>? ended = network.FinishOperation(deleteId, null, null);

        Assert.Equal((NetworkOperationKind.Delete, deleteId), (ended!.Kind, ended.Id));
        Assert.True(network.IsTerminal);
        Assert.Null(network.CurrentState);
        Assert.Null(network.Pending);
        foreach (FirewallRule rule in new[] { creating, active, deleting, failedCreate, alreadyGone })
        {
            Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
            Assert.Null(rule.CurrentState);
            Assert.Null(rule.Pending);
        }
    }

    // ---------- a create that fails, and the public /64 ----------

    // Incus never had the network, so nothing holds the /64 there; NetworkRoutes gives the route back on this answer.
    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_WithPublicIpv6_LetsGoOfTheSubnet()
    {
        Network network = WantingPublicIpv6();

        PendingOperation<NetworkOperationKind>? ended = Failed(network, network.Pending!.Id);

        Assert.Equal(NetworkOperationKind.Create, ended!.Kind);
        Assert.Null(network.PublicIpv6Subnet);
        Assert.Null(network.Pending);
        Assert.Null(network.ExternalId);
        Assert.Equal(CurrentExistence.NotCreated, network.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("The network request could not be completed.", network.OperationFailureReason);
    }

    // Incus made the network, router and all, before the command gave up: it exists and keeps its /64 until the delete.
    [Fact]
    public void Failed_OfTheCreate_WithAnId_KeepsTheSubnet_TheIdAndExistence()
    {
        Network network = WantingPublicIpv6();

        PendingOperation<NetworkOperationKind>? ended = network.FailOperation(network.Pending!.Id, "net-ext", Seen(NetworkStatus.Errored), "Network creation failed.", "raw");

        Assert.Equal(NetworkOperationKind.Create, ended!.Kind);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
        Assert.Equal(("net-ext", CurrentExistence.Exists, (NetworkStatus?)NetworkStatus.Errored),
            (network.ExternalId, network.CurrentExistence, network.CurrentState));
        Assert.Equal("10.0.0.0/24", network.Cidr);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("Network creation failed.", network.OperationFailureReason);
        Assert.Equal("net-ext", network.Delete()!.ProviderId);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutPublicIpv6_LetsGoOfNothing()
    {
        Network network = NewNetwork();

        Assert.NotNull(Failed(network, network.Pending!.Id));
        Assert.False(network.HasPublicIpv6);
    }

    // ---------- Transition follows the kind ----------

    [Fact]
    public void Transition_FollowsTheKindOfTheWaitingRequest()
    {
        Network network = NewNetwork();
        Assert.Equal((NetworkOperationKind.Create, NetworkTransition.Creating), (network.Pending!.Kind, network.Transition));

        network.FinishOperation(network.Pending.Id, "net-ext", Seen(NetworkStatus.Created));
        Assert.Null(network.Transition);

        network.Delete();
        Assert.Equal((NetworkOperationKind.Delete, NetworkTransition.Deleting), (network.Pending!.Kind, network.Transition));

        FirewallRule rule = NewRule(UsableNetwork(), 22);
        Assert.Equal((FirewallRuleOperationKind.Create, FirewallRuleTransition.Creating), (rule.Pending!.Kind, rule.Transition));
    }

    // ---------- a firewall rule's requests, through the network ----------

    [Fact]
    public void RuleDone_OfTheCreate_RecordsTheIdExistenceAndState_AndEndsIt()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;

        PendingOperation<FirewallRuleOperationKind>? ended = network.FinishFirewallRuleOperation(rule.Id, createId, "acl-net/22", FirewallRuleState.logged);

        Assert.Equal((FirewallRuleOperationKind.Create, createId), (ended!.Kind, ended.Id));
        Assert.Null(rule.Pending);
        Assert.Null(rule.Transition);
        Assert.Equal(("acl-net/22", CurrentExistence.Exists, (FirewallRuleState?)FirewallRuleState.logged),
            (rule.ExternalId, rule.CurrentExistence, rule.CurrentState));
        Assert.Equal(1, network.FirewallRuleQuotaUsage);
    }

    [Fact]
    public void RuleDone_OfTheCreate_WithoutAState_LeavesTheStateToTheReconciler()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);

        network.FinishFirewallRuleOperation(rule.Id, rule.Pending!.Id, "acl-net/22", null);

        Assert.Null(rule.Pending);
        Assert.Null(rule.CurrentState);
        Assert.Equal(("acl-net/22", CurrentExistence.Exists), (rule.ExternalId, rule.CurrentExistence));
    }

    [Fact]
    public void RuleDone_OfTheDelete_EndsTheRule()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        OperationTicket delete = network.DeleteFirewallRule(rule.Id)!;

        PendingOperation<FirewallRuleOperationKind>? ended = network.FinishFirewallRuleOperation(rule.Id, delete.OperationId, null, null);

        Assert.Equal((FirewallRuleOperationKind.Delete, delete.OperationId), (ended!.Kind, ended.Id));
        Assert.True(rule.IsTerminal);
        Assert.Null(rule.CurrentState);
        Assert.Null(rule.Pending);
        Assert.Equal(0, network.FirewallRuleQuotaUsage);
        Assert.False(network.IsTerminal);
    }

    [Fact]
    public void RuleFailed_OfTheDelete_GivesTheRuleBack_WithTheReason_AndTheNextDeleteIsANewRequest()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        OperationTicket first = network.DeleteFirewallRule(rule.Id)!;
        Assert.Equal(0, network.FirewallRuleQuotaUsage);

        RuleFailed(network, rule.Id, first.OperationId);

        Assert.Null(rule.Pending);
        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Equal((CurrentExistence.Exists, (FirewallRuleState?)FirewallRuleState.enabled), (rule.CurrentExistence, rule.CurrentState));
        Assert.Equal(("The firewall rule request could not be completed.", "raw"), (rule.OperationFailureReason, rule.OperationFailureDetail));
        Assert.NotNull(rule.OperationFailedAt);
        Assert.Equal(1, network.FirewallRuleQuotaUsage);

        OperationTicket second = network.DeleteFirewallRule(rule.Id)!;
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(second.OperationId, rule.Pending!.Id);
        Assert.Equal("acl-net/22", second.ProviderId);
    }

    // The rule is in the ACL although the command gave up: it keeps the id the worker found, so its delete reaches Incus.
    [Fact]
    public void RuleFailed_OfTheCreate_KeepsAnIdTheWorkerFound()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);

        RuleFailed(network, rule.Id, rule.Pending!.Id, "acl-net/22");

        Assert.Null(rule.Pending);
        Assert.Equal(("acl-net/22", CurrentExistence.Exists), (rule.ExternalId, rule.CurrentExistence));
        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Equal("The firewall rule request could not be completed.", rule.OperationFailureReason);
        Assert.Equal("acl-net/22", network.DeleteFirewallRule(rule.Id)!.ProviderId);
    }

    [Fact]
    public void RuleReport_WithAnotherId_OrNothingWaiting_ChangesNothing_AndReturnsNull()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;

        Assert.Null(network.FinishFirewallRuleOperation(rule.Id, Guid.NewGuid(), "acl-net/x", null));
        Assert.Equal(createId, rule.Pending!.Id);
        Assert.Null(rule.ExternalId);

        network.FinishFirewallRuleOperation(rule.Id, createId, "acl-net/22", FirewallRuleState.enabled);

        Assert.Null(RuleFailed(network, rule.Id, createId, "acl-net/other"));
        Assert.Equal("acl-net/22", rule.ExternalId);
        Assert.Null(rule.OperationFailureReason);
    }

    [Theory]
    [InlineData(FirewallRuleState.enabled)]
    [InlineData(FirewallRuleState.disabled)]
    [InlineData(FirewallRuleState.logged)]
    public void ARuleObservation_WritesCurrentOnly_AndNeverEndsTheRulesCreate(FirewallRuleState state)
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;

        network.MarkFirewallRuleObserved(rule.Id, state);

        Assert.Equal((FirewallRuleOperationKind.Create, createId), (rule.Pending!.Kind, rule.Pending.Id));
        Assert.Equal(FirewallRuleTransition.Creating, rule.Transition);
        Assert.Equal((CurrentExistence.Exists, (FirewallRuleState?)state), (rule.CurrentExistence, rule.CurrentState));
        Assert.Null(rule.ExternalId);
    }

    [Fact]
    public void ARuleNotFound_WritesCurrentOnly_AndNeverEndsTheRulesCreate()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;

        network.MarkFirewallRuleNotFound(rule.Id);

        Assert.Equal(createId, rule.Pending!.Id);
        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        Assert.Null(rule.OperationFailureReason);
    }

    // A rule whose create still waited went with its network's ACL; the create's late report brings nothing back.
    [Fact]
    public void ARuleReport_AfterTheRuleWentWithItsNetwork_IsIgnored()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;
        network.FinishOperation(network.Delete()!.OperationId, null, null);

        Assert.Null(network.FinishFirewallRuleOperation(rule.Id, createId, "acl-net/22", FirewallRuleState.enabled));
        network.MarkFirewallRuleObserved(rule.Id, FirewallRuleState.enabled);

        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Null(rule.ExternalId);
        Assert.Null(rule.CurrentState);
    }

    // The rule's request is its own: ending it leaves the network's alone, and the network's leaves the rule's.
    [Fact]
    public void ARulesRequest_AndItsNetworksRequest_EndIndependently()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        OperationTicket ruleDelete = network.DeleteFirewallRule(rule.Id)!;
        Guid networkDelete = network.Delete()!.OperationId;

        Assert.Null(network.FinishOperation(ruleDelete.OperationId, null, null));
        Assert.Null(network.FinishFirewallRuleOperation(rule.Id, networkDelete, null, null));
        Assert.Equal(networkDelete, network.Pending!.Id);
        Assert.Equal(ruleDelete.OperationId, rule.Pending!.Id);

        RuleFailed(network, rule.Id, ruleDelete.OperationId);

        Assert.Equal(networkDelete, network.Pending!.Id);
        Assert.Null(rule.Pending);
    }
}
