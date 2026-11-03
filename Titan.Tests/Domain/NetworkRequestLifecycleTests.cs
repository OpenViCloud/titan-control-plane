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
        network.EndRequest(network.Pending!.Id, NetworkRequestEnd.Done("net-ext", Seen(NetworkStatus.Created)));
        return network;
    }

    private static NetworkRequestEnd Failed(string? externalId = null) =>
        NetworkRequestEnd.Failed(externalId, null, "The network request could not be completed.", "raw");

    private static FirewallRule NewRule(Network network, int port) =>
        network.AddFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, PortRange.Of(port, port),
            FirewallCidr.Parse("0.0.0.0/0")).Rule;

    private static FirewallRule ActiveRule(Network network, int port)
    {
        FirewallRule rule = NewRule(network, port);
        network.EndFirewallRuleRequest(rule.Id, rule.Pending!.Id,
            FirewallRuleRequestEnd.Done($"acl-net/{port}", FirewallRuleState.enabled));
        return rule;
    }

    private static FirewallRuleRequestEnd RuleFailed(string? externalId = null) =>
        FirewallRuleRequestEnd.Failed(externalId, "The firewall rule request could not be completed.", "raw");

    // ---------- the network's create ----------

    [Fact]
    public void ANewNetwork_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        Network network = NewNetwork();

        Assert.Equal(NetworkRequestKind.Create, network.Pending!.Kind);
        Assert.NotEqual(Guid.Empty, network.Pending.Id);
        Assert.True(network.Pending.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdExistenceAddressAndStatus_AndEndsIt()
    {
        Network network = NewNetwork();
        Guid createId = network.Pending!.Id;

        NetworkRequestEnded? ended = network.EndRequest(createId,
            NetworkRequestEnd.Done("net-ext", Seen(NetworkStatus.Created)));

        Assert.Equal((NetworkRequestKind.Create, createId), (ended!.Request.Kind, ended.Request.Id));
        Assert.Empty(ended.RulesWhoseDeleteEnded);
        Assert.False(ended.LetGoOfPublicIpv6);
        Assert.Null(network.Pending);
        Assert.Null(network.Transition);
        Assert.Equal(("net-ext", CurrentExistence.Exists, NetworkStatus.Created),
            (network.ExternalId, network.CurrentExistence, network.CurrentState));
        Assert.Equal(("10.0.0.0/24", "10.0.0.1"), (network.Cidr, network.Gateway));
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("net-ext", network.BeginVmDeployment("cluster-1"));
    }

    // A worker from before the request routes reports the create's Done with no status (the old Created): the network
    // exists, and is usable only once the reconciler reads it Created.
    [Fact]
    public void Done_OfTheCreate_WithoutAStatus_LeavesTheStateNull_AndTheNetworkNotUsable_UntilTheReconcilerReadsCreated()
    {
        Network network = NewNetwork();

        network.EndRequest(network.Pending!.Id, NetworkRequestEnd.Done("net-ext", Seen(null)));

        Assert.Null(network.Pending);
        Assert.Null(network.CurrentState);
        Assert.Equal(("net-ext", CurrentExistence.Exists), (network.ExternalId, network.CurrentExistence));
        Assert.Equal("FIREWALL_NETWORK_NOT_READY", Assert.Throws<BusinessException>(() => NewRule(network, 22)).Code);
        Assert.Equal("NETWORK_NOT_ACTIVE",
            Assert.Throws<BusinessException>(() => network.BeginVmDeployment("cluster-1")).Code);

        network.MarkObserved(NetworkStatus.Created);

        Assert.Equal("net-ext", network.BeginVmDeployment("cluster-1"));
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

        Assert.Equal((NetworkRequestKind.Create, createId), (network.Pending!.Kind, network.Pending.Id));
        Assert.Equal(NetworkTransition.Creating, network.Transition);
        Assert.Equal((CurrentExistence.Exists, (NetworkStatus?)status), (network.CurrentExistence, network.CurrentState));
        Assert.Null(network.ExternalId);
        Assert.Null(network.RequestFailureReason);
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
        Assert.Null(network.RequestFailureReason);
    }

    // ---------- only the report with the id ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsNull()
    {
        Network network = NewNetwork();
        Guid createId = network.Pending!.Id;

        NetworkRequestEnded? ended = network.EndRequest(Guid.NewGuid(),
            NetworkRequestEnd.Done("net-ext", Seen(NetworkStatus.Created)));

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
        network.EndRequest(createId, NetworkRequestEnd.Done("net-ext", Seen(NetworkStatus.Created)));

        Assert.Null(network.EndRequest(createId, Failed("other")));
        Assert.Null(network.EndRequest(Guid.NewGuid(), Failed()));

        Assert.Equal("net-ext", network.ExternalId);
        Assert.Null(network.RequestFailureReason);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    [Fact]
    public void AReport_AfterDeleted_IsIgnored()
    {
        Network network = UsableNetwork();
        Guid deleteId = network.RequestDelete().RequestId;
        network.EndRequest(deleteId, NetworkRequestEnd.Done(null, null));

        Assert.Null(network.EndRequest(deleteId, Failed()));
        Assert.Null(network.EndRequest(Guid.NewGuid(), NetworkRequestEnd.Done("other", Seen(NetworkStatus.Created))));
        network.MarkObserved(NetworkStatus.Created);
        network.MarkNotFound();

        Assert.Equal((CurrentExistence.Deleted, (NetworkStatus?)null), (network.CurrentExistence, network.CurrentState));
        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Equal("net-ext", network.ExternalId);
        Assert.Null(network.RequestFailureReason);
        Assert.Null(network.Pending);
    }

    // ---------- the network's delete ----------

    [Fact]
    public void RequestDelete_EveryTime_GivesAFreshRequestId_TheOneTheNetworkWaitsOn()
    {
        Network network = UsableNetwork();

        RequestTicket first = network.RequestDelete();
        Assert.Equal((NetworkRequestKind.Delete, first.RequestId), (network.Pending!.Kind, network.Pending.Id));
        network.EndRequest(first.RequestId, Failed());
        RequestTicket second = network.RequestDelete();

        Assert.NotEqual(Guid.Empty, first.RequestId);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(second.RequestId, network.Pending!.Id);
        Assert.Equal(("net-ext", "net-ext"), (first.ProviderId, second.ProviderId));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheNetworkBack_WithTheReason_AndTheNextDeleteIsANewRequest()
    {
        Network network = UsableNetwork();
        Guid firstDelete = network.RequestDelete().RequestId;

        NetworkRequestEnded? ended = network.EndRequest(firstDelete, Failed());

        Assert.Equal(NetworkRequestKind.Delete, ended!.Request.Kind);
        Assert.Null(network.Pending);
        Assert.Null(network.Transition);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal((CurrentExistence.Exists, (NetworkStatus?)NetworkStatus.Created), (network.CurrentExistence, network.CurrentState));
        Assert.Equal(("The network request could not be completed.", "raw"),
            (network.RequestFailureReason, network.RequestFailureDetail));
        Assert.NotNull(network.RequestFailedAt);

        Guid secondDelete = network.RequestDelete().RequestId;

        Assert.NotEqual(firstDelete, secondDelete);
        Assert.Null(network.EndRequest(firstDelete, Failed())); // the first delete's late report is stale
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void Done_AfterAnOlderFailure_ClearsIt()
    {
        Network network = UsableNetwork();
        network.EndRequest(network.RequestDelete().RequestId, Failed());

        network.EndRequest(network.RequestDelete().RequestId, NetworkRequestEnd.Done(null, null));

        Assert.Null(network.RequestFailureReason);
        Assert.Null(network.RequestFailureDetail);
        Assert.Null(network.RequestFailedAt);
    }

    // The delete removed the network's ACL and every rule in it (titan-ops features/network/design.md#network-lifecycle).
    [Fact]
    public void Done_OfTheDelete_EndsTheNetworkAndEveryRule_NamingOnlyTheRulesWhoseOwnDeleteWaited()
    {
        Network network = UsableNetwork();
        FirewallRule creating = NewRule(network, 21);
        FirewallRule active = ActiveRule(network, 22);
        FirewallRule deleting = ActiveRule(network, 23);
        network.RequestFirewallRuleDelete(deleting.Id);
        FirewallRule failedCreate = NewRule(network, 24);
        network.EndFirewallRuleRequest(failedCreate.Id, failedCreate.Pending!.Id, RuleFailed());
        FirewallRule alreadyGone = ActiveRule(network, 25);
        network.EndFirewallRuleRequest(alreadyGone.Id, network.RequestFirewallRuleDelete(alreadyGone.Id).RequestId,
            FirewallRuleRequestEnd.Done(null, null));
        Guid deleteId = network.RequestDelete().RequestId;

        NetworkRequestEnded? ended = network.EndRequest(deleteId, NetworkRequestEnd.Done(null, null));

        Assert.Equal((NetworkRequestKind.Delete, deleteId), (ended!.Request.Kind, ended.Request.Id));
        Assert.Equal([deleting.Id], ended.RulesWhoseDeleteEnded.Select(r => r.Id));
        Assert.False(ended.LetGoOfPublicIpv6);
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
    public void Failed_OfTheCreate_WithoutAnId_WithPublicIpv6_LetsGoOfTheSubnet_AndSaysSo()
    {
        Network network = WantingPublicIpv6();

        NetworkRequestEnded? ended = network.EndRequest(network.Pending!.Id, Failed());

        Assert.True(ended!.LetGoOfPublicIpv6);
        Assert.Null(network.PublicIpv6Subnet);
        Assert.Null(network.Pending);
        Assert.Null(network.ExternalId);
        Assert.Equal(CurrentExistence.NotCreated, network.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("The network request could not be completed.", network.RequestFailureReason);
    }

    // Incus made the network, router and all, before the command gave up: it exists and keeps its /64 until the delete.
    [Fact]
    public void Failed_OfTheCreate_WithAnId_KeepsTheSubnet_TheIdAndExistence()
    {
        Network network = WantingPublicIpv6();

        NetworkRequestEnded? ended = network.EndRequest(network.Pending!.Id,
            NetworkRequestEnd.Failed("net-ext", Seen(NetworkStatus.Errored), "Network creation failed.", "raw"));

        Assert.False(ended!.LetGoOfPublicIpv6);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
        Assert.Equal(("net-ext", CurrentExistence.Exists, (NetworkStatus?)NetworkStatus.Errored),
            (network.ExternalId, network.CurrentExistence, network.CurrentState));
        Assert.Equal("10.0.0.0/24", network.Cidr);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("Network creation failed.", network.RequestFailureReason);
        Assert.Equal("net-ext", network.RequestDelete().ProviderId);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutPublicIpv6_LetsGoOfNothing()
    {
        Network network = NewNetwork();

        Assert.False(network.EndRequest(network.Pending!.Id, Failed())!.LetGoOfPublicIpv6);
    }

    // ---------- Transition follows the kind ----------

    [Fact]
    public void Transition_FollowsTheKindOfTheWaitingRequest()
    {
        Network network = NewNetwork();
        Assert.Equal((NetworkRequestKind.Create, NetworkTransition.Creating), (network.Pending!.Kind, network.Transition));

        network.EndRequest(network.Pending.Id, NetworkRequestEnd.Done("net-ext", Seen(NetworkStatus.Created)));
        Assert.Null(network.Transition);

        network.RequestDelete();
        Assert.Equal((NetworkRequestKind.Delete, NetworkTransition.Deleting), (network.Pending!.Kind, network.Transition));

        FirewallRule rule = NewRule(UsableNetwork(), 22);
        Assert.Equal((FirewallRuleRequestKind.Create, FirewallRuleTransition.Creating), (rule.Pending!.Kind, rule.Transition));
    }

    // ---------- a firewall rule's requests, through the network ----------

    [Fact]
    public void RuleDone_OfTheCreate_RecordsTheIdExistenceAndState_AndEndsIt()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;

        PendingRequest<FirewallRuleRequestKind>? ended = network.EndFirewallRuleRequest(rule.Id, createId,
            FirewallRuleRequestEnd.Done("acl-net/22", FirewallRuleState.logged));

        Assert.Equal((FirewallRuleRequestKind.Create, createId), (ended!.Kind, ended.Id));
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

        network.EndFirewallRuleRequest(rule.Id, rule.Pending!.Id, FirewallRuleRequestEnd.Done("acl-net/22", null));

        Assert.Null(rule.Pending);
        Assert.Null(rule.CurrentState);
        Assert.Equal(("acl-net/22", CurrentExistence.Exists), (rule.ExternalId, rule.CurrentExistence));
    }

    [Fact]
    public void RuleDone_OfTheDelete_EndsTheRule()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        RequestTicket delete = network.RequestFirewallRuleDelete(rule.Id);

        PendingRequest<FirewallRuleRequestKind>? ended = network.EndFirewallRuleRequest(rule.Id, delete.RequestId,
            FirewallRuleRequestEnd.Done(null, null));

        Assert.Equal((FirewallRuleRequestKind.Delete, delete.RequestId), (ended!.Kind, ended.Id));
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
        RequestTicket first = network.RequestFirewallRuleDelete(rule.Id);
        Assert.Equal(0, network.FirewallRuleQuotaUsage);

        network.EndFirewallRuleRequest(rule.Id, first.RequestId, RuleFailed());

        Assert.Null(rule.Pending);
        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Equal((CurrentExistence.Exists, (FirewallRuleState?)FirewallRuleState.enabled), (rule.CurrentExistence, rule.CurrentState));
        Assert.Equal(("The firewall rule request could not be completed.", "raw"), (rule.RequestFailureReason, rule.RequestFailureDetail));
        Assert.NotNull(rule.RequestFailedAt);
        Assert.Equal(1, network.FirewallRuleQuotaUsage);

        RequestTicket second = network.RequestFirewallRuleDelete(rule.Id);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal(second.RequestId, rule.Pending!.Id);
        Assert.Equal("acl-net/22", second.ProviderId);
    }

    // The rule is in the ACL although the command gave up: it keeps the id the worker found, so its delete reaches Incus.
    [Fact]
    public void RuleFailed_OfTheCreate_KeepsAnIdTheWorkerFound()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);

        network.EndFirewallRuleRequest(rule.Id, rule.Pending!.Id, RuleFailed("acl-net/22"));

        Assert.Null(rule.Pending);
        Assert.Equal(("acl-net/22", CurrentExistence.Exists), (rule.ExternalId, rule.CurrentExistence));
        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Equal("The firewall rule request could not be completed.", rule.RequestFailureReason);
        Assert.Equal("acl-net/22", network.RequestFirewallRuleDelete(rule.Id).ProviderId);
    }

    [Fact]
    public void RuleReport_WithAnotherId_OrNothingWaiting_ChangesNothing_AndReturnsNull()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;

        Assert.Null(network.EndFirewallRuleRequest(rule.Id, Guid.NewGuid(), FirewallRuleRequestEnd.Done("acl-net/x", null)));
        Assert.Equal(createId, rule.Pending!.Id);
        Assert.Null(rule.ExternalId);

        network.EndFirewallRuleRequest(rule.Id, createId, FirewallRuleRequestEnd.Done("acl-net/22", FirewallRuleState.enabled));

        Assert.Null(network.EndFirewallRuleRequest(rule.Id, createId, RuleFailed("acl-net/other")));
        Assert.Equal("acl-net/22", rule.ExternalId);
        Assert.Null(rule.RequestFailureReason);
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

        Assert.Equal((FirewallRuleRequestKind.Create, createId), (rule.Pending!.Kind, rule.Pending.Id));
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
        Assert.Null(rule.RequestFailureReason);
    }

    // A rule whose create still waited went with its network's ACL; the create's late report brings nothing back.
    [Fact]
    public void ARuleReport_AfterTheRuleWentWithItsNetwork_IsIgnored()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Pending!.Id;
        network.EndRequest(network.RequestDelete().RequestId, NetworkRequestEnd.Done(null, null));

        Assert.Null(network.EndFirewallRuleRequest(rule.Id, createId,
            FirewallRuleRequestEnd.Done("acl-net/22", FirewallRuleState.enabled)));
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
        RequestTicket ruleDelete = network.RequestFirewallRuleDelete(rule.Id);
        Guid networkDelete = network.RequestDelete().RequestId;

        Assert.Null(network.EndRequest(ruleDelete.RequestId, NetworkRequestEnd.Done(null, null)));
        Assert.Null(network.EndFirewallRuleRequest(rule.Id, networkDelete, FirewallRuleRequestEnd.Done(null, null)));
        Assert.Equal(networkDelete, network.Pending!.Id);
        Assert.Equal(ruleDelete.RequestId, rule.Pending!.Id);

        network.EndFirewallRuleRequest(rule.Id, ruleDelete.RequestId, RuleFailed());

        Assert.Equal(networkDelete, network.Pending!.Id);
        Assert.Null(rule.Pending);
    }
}
