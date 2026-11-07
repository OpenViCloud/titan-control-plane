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

    private static Network UsableNetwork()
    {
        Network network = NewNetwork();
        network.ReportDone(network.Operation.PendingId!.Value, "net-ext", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    private static NetworkStatus? Failed(Network network, Guid operationId,
        string? externalId = null) =>
        network.ReportFailed(operationId, externalId, null, null, null, "The network request could not be completed.", "raw");

    private static FirewallRule NewRule(Network network, int port) =>
        network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, PortRange.Of(port, port),
            FirewallCidr.Parse("0.0.0.0/0"));

    private static FirewallRule ActiveRule(Network network, int port)
    {
        FirewallRule rule = NewRule(network, port);
        network.ReportRuleDone(rule.Id, rule.Operation.PendingId!.Value, $"acl-net/{port}", FirewallRuleStatus.enabled);
        return rule;
    }

    private static FirewallRuleStatus? RuleFailed(Network network, Guid ruleId, Guid operationId,
        string? externalId = null) =>
        network.ReportRuleFailed(ruleId, operationId, externalId, null,
            "The firewall rule request could not be completed.", "raw");

    // ---------- the network's create ----------

    [Fact]
    public void ANewNetwork_WaitsOnItsCreate_WithAnIdAKindAndATime()
    {
        Network network = NewNetwork();

        Assert.Equal(NetworkStatus.Creating, network.Status);
        Assert.NotEqual(Guid.Empty, network.Operation.PendingId!.Value);
        Assert.True(network.Operation.AskedAt <= DateTimeOffset.UtcNow);
        Assert.Equal(NetworkStatus.Creating, network.Status);
    }

    [Fact]
    public void Done_OfTheCreate_RecordsTheIdExistenceAddressAndStatus_AndEndsIt()
    {
        Network network = NewNetwork();
        Guid createId = network.Operation.PendingId!.Value;

        NetworkStatus? ended = network.ReportDone(createId, "net-ext", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");

        Assert.Equal(NetworkStatus.Creating, ended);
        Assert.Null(network.Operation.PendingId);
        Assert.True(network.Status != NetworkStatus.Creating && network.Status != NetworkStatus.Deleting);
        Assert.Equal(("net-ext", NetworkStatus.Created), (network.ExternalId, network.Status));
        Assert.Equal(("10.0.0.0/24", "10.0.0.1"), (network.Cidr, network.Gateway));
        Assert.False(network.IsDeletedOrDeleting);
        Assert.Equal("net-ext", network.ProviderIdForVm("cluster-1"));
    }

    // A worker from before the request routes reports the create's Done with no status (the old Created): the network
    // exists, and is usable only once the reconciler reads it Created.
    [Fact]
    public void Done_OfTheCreate_WithoutAStatus_LeavesTheStateNull_AndTheNetworkNotUsable_UntilTheReconcilerReadsCreated()
    {
        Network network = NewNetwork();

        network.ReportDone(network.Operation.PendingId!.Value, "net-ext", null, "10.0.0.0/24", "10.0.0.1");

        Assert.Null(network.Operation.PendingId);
        Assert.Equal(("net-ext", NetworkStatus.Unknown), (network.ExternalId, network.Status));
        Assert.Equal("FIREWALL_NETWORK_NOT_READY", Assert.Throws<BusinessException>(() => NewRule(network, 22)).Code);
        Assert.Equal("NETWORK_NOT_ACTIVE",
            Assert.Throws<BusinessException>(() => network.ProviderIdForVm("cluster-1")).Code);

        network.MarkObserved(NetworkStatus.Created);

        Assert.Equal("net-ext", network.ProviderIdForVm("cluster-1"));
        Assert.Equal("net-ext", network.AddFirewallRule(Guid.NewGuid(), FirewallDirection.Ingress, FirewallProtocol.TCP,
            PortRange.Of(22, 22), FirewallCidr.Parse("0.0.0.0/0")).ProviderId);
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
        Guid createId = network.Operation.PendingId!.Value;

        network.MarkObserved(status);

        Assert.Equal((NetworkStatus.Creating, createId), (network.Status, network.Operation.PendingId!.Value));
        Assert.Equal(NetworkStatus.Creating, network.Status);
        Assert.Equal((NetworkStatus?)status, network.Operation.LastSeenStatus);
        Assert.Null(network.ExternalId);
        Assert.Null(network.Operation.FailureReason);
        // A network still waiting on its create is not usable, even read Created.
        Assert.Equal("FIREWALL_NETWORK_NOT_READY", Assert.Throws<BusinessException>(() => NewRule(network, 22)).Code);
    }

    [Fact]
    public void NotFound_WhileTheCreateWaits_RecordsNoStatus_AndLeavesTheCreateWaiting()
    {
        Network network = NewNetwork();
        Guid createId = network.Operation.PendingId!.Value;

        network.MarkNotFound();

        Assert.Equal(createId, network.Operation.PendingId!.Value);
        Assert.Equal(NetworkStatus.Creating, network.Status);
        Assert.Null(network.Operation.LastSeenStatus);
        Assert.Null(network.Operation.FailureReason);
    }

    // ---------- only the report with the id ----------

    [Fact]
    public void AReport_WithAnotherId_ChangesNothing_AndReturnsNull()
    {
        Network network = NewNetwork();
        Guid createId = network.Operation.PendingId!.Value;

        NetworkStatus? ended = network.ReportDone(Guid.NewGuid(), "net-ext", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");

        Assert.Null(ended);
        Assert.Equal(createId, network.Operation.PendingId!.Value);
        Assert.Null(network.ExternalId);
        Assert.Null(network.Cidr);
        Assert.Null(network.InstanceStatus);
        Assert.True(network.Status == NetworkStatus.Creating || network.Status == NetworkStatus.Failed);
    }

    [Fact]
    public void AReport_WithNothingWaiting_ChangesNothing_AndReturnsNull()
    {
        Network network = NewNetwork();
        Guid createId = network.Operation.PendingId!.Value;
        network.ReportDone(createId, "net-ext", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");

        Assert.Null(Failed(network, createId, "other"));
        Assert.Null(Failed(network, Guid.NewGuid()));

        Assert.Equal("net-ext", network.ExternalId);
        Assert.Null(network.Operation.FailureReason);
        Assert.False(network.IsDeletedOrDeleting);
        Assert.Equal(NetworkStatus.Created, network.InstanceStatus);
    }

    [Fact]
    public void AReport_AfterDeleted_IsIgnored()
    {
        Network network = UsableNetwork();
        Guid deleteId = network.Delete()!.OperationId;
        network.ReportDone(deleteId, null, null, null, null);

        Assert.Null(Failed(network, deleteId));
        Assert.Null(network.ReportDone(Guid.NewGuid(), "other", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1"));
        network.MarkObserved(NetworkStatus.Created);
        network.MarkNotFound();

        Assert.Equal((NetworkStatus.Deleted, (NetworkStatus?)null), (network.Status, network.InstanceStatus));
        Assert.True(network.IsDeletedOrDeleting);
        Assert.Equal("net-ext", network.ExternalId);
        Assert.Null(network.Operation.FailureReason);
        Assert.Null(network.Operation.PendingId);
    }

    // ---------- the network's delete ----------

    [Fact]
    public void RequestDelete_EveryTime_GivesAFreshRequestId_TheOneTheNetworkWaitsOn()
    {
        Network network = UsableNetwork();

        OperationTicket first = network.Delete()!;
        Assert.Equal((NetworkStatus.Deleting, first.OperationId), (network.Status, network.Operation.PendingId!.Value));
        Failed(network, first.OperationId);
        OperationTicket second = network.Delete()!;

        Assert.NotEqual(Guid.Empty, first.OperationId);
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(second.OperationId, network.Operation.PendingId!.Value);
        Assert.Equal(("net-ext", "net-ext"), (first.ProviderId, second.ProviderId));
    }

    [Fact]
    public void Failed_OfTheDelete_GivesTheNetworkBack_WithTheReason_AndTheNextDeleteIsANewRequest()
    {
        Network network = UsableNetwork();
        Guid firstDelete = network.Delete()!.OperationId;

        NetworkStatus? ended = Failed(network, firstDelete);

        Assert.Equal(NetworkStatus.Deleting, ended);
        Assert.Null(network.Operation.PendingId);
        Assert.True(network.Status != NetworkStatus.Creating && network.Status != NetworkStatus.Deleting);
        Assert.False(network.IsDeletedOrDeleting);
        Assert.Equal(NetworkStatus.Created, network.Status);
        Assert.Equal(("The network request could not be completed.", "raw"),
            (network.Operation.FailureReason, network.Operation.FailureDetail));
        Assert.NotNull(network.Operation.FailedAt);

        Guid secondDelete = network.Delete()!.OperationId;

        Assert.NotEqual(firstDelete, secondDelete);
        Assert.Null(Failed(network, firstDelete)); // the first delete's late report is stale
        Assert.Equal(NetworkStatus.Deleting, network.Status);
    }

    [Fact]
    public void Done_AfterAnOlderFailure_ClearsIt()
    {
        Network network = UsableNetwork();
        Failed(network, network.Delete()!.OperationId);

        network.ReportDone(network.Delete()!.OperationId, null, null, null, null);

        Assert.Null(network.Operation.FailureReason);
        Assert.Null(network.Operation.FailureDetail);
        Assert.Null(network.Operation.FailedAt);
    }

    // The delete removed the network's ACL and every rule in it (titan-ops features/network/design.md#network-lifecycle).
    [Fact]
    public void Done_OfTheDelete_EndsTheNetworkAndEveryRule()
    {
        Network network = UsableNetwork();
        FirewallRule creating = NewRule(network, 21);
        FirewallRule active = ActiveRule(network, 22);
        FirewallRule deleting = ActiveRule(network, 23);
        network.DeleteFirewallRule(deleting.Id);
        FirewallRule failedCreate = NewRule(network, 24);
        RuleFailed(network, failedCreate.Id, failedCreate.Operation.PendingId!.Value);
        FirewallRule alreadyGone = ActiveRule(network, 25);
        network.ReportRuleDone(alreadyGone.Id, network.DeleteFirewallRule(alreadyGone.Id)!.OperationId, null, null);
        Guid deleteId = network.Delete()!.OperationId;

        NetworkStatus? ended = network.ReportDone(deleteId, null, null, null, null);

        Assert.Equal(NetworkStatus.Deleting, ended);
        Assert.True(network.IsDeleted);
        Assert.Null(network.InstanceStatus);
        Assert.Null(network.Operation.PendingId);
        foreach (FirewallRule rule in new[] { creating, active, deleting, failedCreate, alreadyGone })
        {
            Assert.Equal(FirewallRuleStatus.Deleted, rule.Status);
            Assert.Null(rule.InstanceStatus);
            Assert.Null(rule.Operation.PendingId);
        }
    }

    // ---------- a create that fails, and the public /64 ----------

    // Incus never had the network, so nothing holds the /64 there; NetworkRoutes gives the route back on this answer.
    [Fact]
    public void Failed_OfTheCreate_WithoutAnId_WithPublicIpv6_LetsGoOfTheSubnet()
    {
        Network network = WantingPublicIpv6();

        NetworkStatus? ended = Failed(network, network.Operation.PendingId!.Value);

        Assert.Equal(NetworkStatus.Creating, ended);
        Assert.Null(network.PublicIpv6Subnet);
        Assert.Null(network.Operation.PendingId);
        Assert.Null(network.ExternalId);
        Assert.True(network.Status == NetworkStatus.Creating || network.Status == NetworkStatus.Failed);
        Assert.False(network.IsDeletedOrDeleting);
        Assert.Equal("The network request could not be completed.", network.Operation.FailureReason);
    }

    // Incus made the network, router and all, before the command gave up: it exists and keeps its /64 until the delete.
    [Fact]
    public void Failed_OfTheCreate_WithAnId_KeepsTheSubnet_TheIdAndExistence()
    {
        Network network = WantingPublicIpv6();

        NetworkStatus? ended = network.ReportFailed(network.Operation.PendingId!.Value, "net-ext", NetworkStatus.Errored, "10.0.0.0/24", "10.0.0.1", "Network creation failed.", "raw");

        Assert.Equal(NetworkStatus.Creating, ended);
        Assert.Equal(Subnet, network.PublicIpv6Subnet);
        Assert.Equal(("net-ext", NetworkStatus.Errored), (network.ExternalId, network.Status));
        Assert.Equal("10.0.0.0/24", network.Cidr);
        Assert.False(network.IsDeletedOrDeleting);
        Assert.Equal("Network creation failed.", network.Operation.FailureReason);
        Assert.Equal("net-ext", network.Delete()!.ProviderId);
    }

    [Fact]
    public void Failed_OfTheCreate_WithoutPublicIpv6_LetsGoOfNothing()
    {
        Network network = NewNetwork();

        Assert.NotNull(Failed(network, network.Operation.PendingId!.Value));
        Assert.False(network.HasPublicIpv6);
    }

    // ---------- Transition follows the kind ----------

    [Fact]
    public void Transition_FollowsTheKindOfTheWaitingRequest()
    {
        Network network = NewNetwork();
        Assert.Equal((NetworkStatus.Creating, NetworkStatus.Creating), (network.Status, network.Status));

        network.ReportDone(network.Operation.PendingId!.Value, "net-ext", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        Assert.True(network.Status != NetworkStatus.Creating && network.Status != NetworkStatus.Deleting);

        network.Delete();
        Assert.Equal((NetworkStatus.Deleting, NetworkStatus.Deleting), (network.Status, network.Status));

        FirewallRule rule = NewRule(UsableNetwork(), 22);
        Assert.Equal((FirewallRuleStatus.Creating, FirewallRuleStatus.Creating), (rule.Status, rule.Status));
    }

    // ---------- a firewall rule's requests, through the network ----------

    [Fact]
    public void RuleDone_OfTheCreate_RecordsTheIdExistenceAndState_AndEndsIt()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Operation.PendingId!.Value;

        FirewallRuleStatus? ended = network.ReportRuleDone(rule.Id, createId, "acl-net/22", FirewallRuleStatus.logged);

        Assert.Equal(FirewallRuleStatus.Creating, ended);
        Assert.Null(rule.Operation.PendingId);
        Assert.True(rule.Status != FirewallRuleStatus.Creating && rule.Status != FirewallRuleStatus.Deleting);
        Assert.Equal(("acl-net/22", FirewallRuleStatus.logged), (rule.ExternalId, rule.Status));
        Assert.Equal(1, network.FirewallRuleQuotaUsage);
    }

    [Fact]
    public void RuleDone_OfTheCreate_WithoutAState_LeavesTheStateToTheReconciler()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);

        network.ReportRuleDone(rule.Id, rule.Operation.PendingId!.Value, "acl-net/22", null);

        Assert.Null(rule.Operation.PendingId);
        Assert.Equal(("acl-net/22", FirewallRuleStatus.Unknown), (rule.ExternalId, rule.Status));
    }

    [Fact]
    public void RuleDone_OfTheDelete_EndsTheRule()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        OperationTicket delete = network.DeleteFirewallRule(rule.Id)!;

        FirewallRuleStatus? ended = network.ReportRuleDone(rule.Id, delete.OperationId, null, null);

        Assert.Equal(FirewallRuleStatus.Deleting, ended);
        Assert.True(rule.IsDeleted);
        Assert.Null(rule.InstanceStatus);
        Assert.Null(rule.Operation.PendingId);
        Assert.Equal(0, network.FirewallRuleQuotaUsage);
        Assert.False(network.IsDeleted);
    }

    [Fact]
    public void RuleFailed_OfTheDelete_GivesTheRuleBack_WithTheReason_AndTheNextDeleteIsANewRequest()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        OperationTicket first = network.DeleteFirewallRule(rule.Id)!;
        Assert.Equal(0, network.FirewallRuleQuotaUsage);

        RuleFailed(network, rule.Id, first.OperationId);

        Assert.Null(rule.Operation.PendingId);
        Assert.False(rule.IsDeletedOrDeleting);
        Assert.Equal(FirewallRuleStatus.enabled, rule.Status);
        Assert.Equal(("The firewall rule request could not be completed.", "raw"), (rule.Operation.FailureReason, rule.Operation.FailureDetail));
        Assert.NotNull(rule.Operation.FailedAt);
        Assert.Equal(1, network.FirewallRuleQuotaUsage);

        OperationTicket second = network.DeleteFirewallRule(rule.Id)!;
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.Equal(second.OperationId, rule.Operation.PendingId!.Value);
        Assert.Equal("acl-net/22", second.ProviderId);
    }

    // The rule is in the ACL although the command gave up: it keeps the id the worker found, so its delete reaches Incus.
    [Fact]
    public void RuleFailed_OfTheCreate_KeepsAnIdTheWorkerFound()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);

        RuleFailed(network, rule.Id, rule.Operation.PendingId!.Value, "acl-net/22");

        Assert.Null(rule.Operation.PendingId);
        Assert.Equal(("acl-net/22", FirewallRuleStatus.Unknown), (rule.ExternalId, rule.Status));
        Assert.False(rule.IsDeletedOrDeleting);
        Assert.Equal("The firewall rule request could not be completed.", rule.Operation.FailureReason);
        Assert.Equal("acl-net/22", network.DeleteFirewallRule(rule.Id)!.ProviderId);
    }

    [Fact]
    public void RuleReport_WithAnotherId_OrNothingWaiting_ChangesNothing_AndReturnsNull()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Operation.PendingId!.Value;

        Assert.Null(network.ReportRuleDone(rule.Id, Guid.NewGuid(), "acl-net/x", null));
        Assert.Equal(createId, rule.Operation.PendingId!.Value);
        Assert.Null(rule.ExternalId);

        network.ReportRuleDone(rule.Id, createId, "acl-net/22", FirewallRuleStatus.enabled);

        Assert.Null(RuleFailed(network, rule.Id, createId, "acl-net/other"));
        Assert.Equal("acl-net/22", rule.ExternalId);
        Assert.Null(rule.Operation.FailureReason);
    }

    [Theory]
    [InlineData(FirewallRuleStatus.enabled)]
    [InlineData(FirewallRuleStatus.disabled)]
    [InlineData(FirewallRuleStatus.logged)]
    public void ARuleObservation_WritesCurrentOnly_AndNeverEndsTheRulesCreate(FirewallRuleStatus state)
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Operation.PendingId!.Value;

        network.MarkFirewallRuleObserved(rule.Id, state);

        Assert.Equal((FirewallRuleStatus.Creating, createId), (rule.Status, rule.Operation.PendingId!.Value));
        Assert.Equal(FirewallRuleStatus.Creating, rule.Status);
        Assert.Equal((FirewallRuleStatus?)state, rule.Operation.LastSeenStatus);
        Assert.Null(rule.ExternalId);
    }

    [Fact]
    public void ARuleNotFound_WhileItsCreateWaits_RecordsNoState_AndNeverEndsTheCreate()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Operation.PendingId!.Value;

        network.MarkFirewallRuleNotFound(rule.Id);

        Assert.Equal(createId, rule.Operation.PendingId!.Value);
        Assert.Equal(FirewallRuleStatus.Creating, rule.Status);
        Assert.Null(rule.Operation.LastSeenStatus);
        Assert.Null(rule.Operation.FailureReason);
    }

    // A rule whose create still waited went with its network's ACL; the create's late report brings nothing back.
    [Fact]
    public void ARuleReport_AfterTheRuleWentWithItsNetwork_IsIgnored()
    {
        Network network = UsableNetwork();
        FirewallRule rule = NewRule(network, 22);
        Guid createId = rule.Operation.PendingId!.Value;
        network.ReportDone(network.Delete()!.OperationId, null, null, null, null);

        Assert.Null(network.ReportRuleDone(rule.Id, createId, "acl-net/22", FirewallRuleStatus.enabled));
        network.MarkFirewallRuleObserved(rule.Id, FirewallRuleStatus.enabled);

        Assert.Equal(FirewallRuleStatus.Deleted, rule.Status);
        Assert.Null(rule.ExternalId);
        Assert.Null(rule.InstanceStatus);
    }

    // The rule's request is its own: ending it leaves the network's alone, and the network's leaves the rule's.
    [Fact]
    public void ARulesRequest_AndItsNetworksRequest_EndIndependently()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        OperationTicket ruleDelete = network.DeleteFirewallRule(rule.Id)!;
        Guid networkDelete = network.Delete()!.OperationId;

        Assert.Null(network.ReportDone(ruleDelete.OperationId, null, null, null, null));
        Assert.Null(network.ReportRuleDone(rule.Id, networkDelete, null, null));
        Assert.Equal(networkDelete, network.Operation.PendingId!.Value);
        Assert.Equal(ruleDelete.OperationId, rule.Operation.PendingId!.Value);

        RuleFailed(network, rule.Id, ruleDelete.OperationId);

        Assert.Equal(networkDelete, network.Operation.PendingId!.Value);
        Assert.Null(rule.Operation.PendingId);
    }

    // The id alone does not end an operation: the report must also be of the kind the network waits on. A delete's Done
    // that carried the id of a create (or the other way round) is ignored, whatever the worker meant by it.
    [Fact]
    public void AReport_OfTheOtherKind_WithTheRightId_ChangesNothing_AndReturnsFalse()
    {
        Network creating = NewNetwork();
        Guid createId = creating.Operation.PendingId!.Value;

        Assert.False(creating.DeleteSucceeded(createId));
        Assert.False(creating.DeleteFailed(createId, null, "reason", "raw"));

        Assert.Equal(createId, creating.Operation.PendingId);
        Assert.Equal(NetworkStatus.Creating, creating.Status);
        Assert.True(creating.Status == NetworkStatus.Creating || creating.Status == NetworkStatus.Failed);
        Assert.Null(creating.Operation.FailureReason);

        Network deleting = UsableNetwork();
        deleting.Delete();
        Guid deleteId = deleting.Operation.PendingId!.Value;

        Assert.False(deleting.CreateSucceeded(deleteId, "other", NetworkStatus.Created, "10.1.0.0/24", "10.1.0.1"));
        Assert.False(deleting.CreateFailed(deleteId, null, null, null, null, "reason", "raw"));

        Assert.Equal(deleteId, deleting.Operation.PendingId);
        Assert.Equal(NetworkStatus.Deleting, deleting.Status);
        Assert.True(deleting.IsDeletedOrDeleting);
        Assert.Equal("net-ext", deleting.ExternalId);
        Assert.Null(deleting.Operation.FailureReason);
    }

    [Fact]
    public void ARuleReport_OfTheOtherKind_WithTheRightId_ChangesNothing_AndReturnsFalse()
    {
        Network network = UsableNetwork();
        FirewallRule creating = NewRule(network, 80);
        Guid createId = creating.Operation.PendingId!.Value;

        Assert.False(network.FirewallRuleDeleteSucceeded(creating.Id, createId));
        Assert.False(network.FirewallRuleDeleteFailed(creating.Id, createId, null, "reason", "raw"));

        Assert.Equal(createId, creating.Operation.PendingId);
        Assert.Equal(FirewallRuleStatus.Creating, creating.Status);
        Assert.True(creating.Status == FirewallRuleStatus.Creating || creating.Status == FirewallRuleStatus.Failed);
        Assert.Null(creating.Operation.FailureReason);

        FirewallRule deleting = ActiveRule(network, 81);
        network.DeleteFirewallRule(deleting.Id);
        Guid deleteId = deleting.Operation.PendingId!.Value;

        Assert.False(network.FirewallRuleCreateSucceeded(deleting.Id, deleteId, "other", FirewallRuleStatus.enabled));
        Assert.False(network.FirewallRuleCreateFailed(deleting.Id, deleteId, null, null, "reason", "raw"));

        Assert.Equal(deleteId, deleting.Operation.PendingId);
        Assert.Equal(FirewallRuleStatus.Deleting, deleting.Status);
        Assert.True(deleting.IsDeletedOrDeleting);
        Assert.Equal("acl-net/81", deleting.ExternalId);
        Assert.Null(deleting.Operation.FailureReason);
    }

    // What a give-up rests in: the word the worker read, else the word Titan held, else Unknown.
    [Fact]
    public void Failed_OfTheDelete_RestsInTheWordTheWorkerReported_BeforeTheOneItHeld()
    {
        Network network = UsableNetwork();
        network.Delete();

        network.ReportFailed(network.Operation.PendingId!.Value, null, NetworkStatus.Errored, null, null, "reason", "raw");

        Assert.Equal(NetworkStatus.Errored, network.Status);
        Assert.Null(network.Operation.LastSeenStatus);
    }

    [Fact]
    public void Failed_OfTheDelete_RestsInTheWordASeenObservationLeft_WhenTheReportBringsNone()
    {
        Network network = UsableNetwork();
        network.Delete();
        network.MarkObserved(NetworkStatus.Unavailable);

        network.ReportFailed(network.Operation.PendingId!.Value, null, null, null, null, "reason", "raw");

        Assert.Equal(NetworkStatus.Unavailable, network.Status);
    }

    [Fact]
    public void Failed_OfTheDelete_OfANetworkNotFoundWhenAsked_RestsInUnknownUntilTheNextObservation()
    {
        Network network = UsableNetwork();
        network.MarkNotFound();
        network.Delete();

        network.ReportFailed(network.Operation.PendingId!.Value, null, null, null, null, "reason", "raw");

        Assert.Equal(NetworkStatus.Unknown, network.Status);
    }

    [Fact]
    public void RuleFailed_OfTheDelete_RestsInTheStateTheWorkerReported_BeforeTheOneItHeld()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        network.DeleteFirewallRule(rule.Id);

        network.ReportRuleFailed(rule.Id, rule.Operation.PendingId!.Value, null, FirewallRuleStatus.logged, "reason", "raw");

        Assert.Equal(FirewallRuleStatus.logged, rule.Status);
    }

    [Fact]
    public void RuleFailed_OfTheDelete_RestsInTheStateASeenObservationLeft_WhenTheReportBringsNone()
    {
        Network network = UsableNetwork();
        FirewallRule rule = ActiveRule(network, 22);
        network.DeleteFirewallRule(rule.Id);
        network.MarkFirewallRuleObserved(rule.Id, FirewallRuleStatus.disabled);

        network.ReportRuleFailed(rule.Id, rule.Operation.PendingId!.Value, null, null, "reason", "raw");

        Assert.Equal(FirewallRuleStatus.disabled, rule.Status);
    }
}
