using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// A firewall rule is the network's child entity (titan-ops architecture/code-structure.md#child-entity): the network
// counts its rules toward the per-network cap, routes each request and observation to the rule it names, and takes
// every rule with it when its own delete is confirmed.
public class NetworkFirewallRuleTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;

    private static Network UsableNetwork()
    {
        var network = TestAggregates.Network(TenantIdOwner, "net-1", "cluster-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    private static FirewallRule Rule(Network network, int port) =>
        network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, port, port, "0.0.0.0/0");

    // The same five situations the repository count once measured (titan-ops features/network/design.md#firewall-rule-quota-counting):
    // creating and held by Incus count; a delete in flight, a Deleted row and a failed create with nothing on Incus do not.
    private static Network NetworkWithEveryRuleSituation()
    {
        var network = UsableNetwork();
        Rule(network, 1); // creating: counts
        Rule(network, 2).MarkActive("sg-2"); // held by Incus: counts
        var deleting = Rule(network, 3);
        deleting.MarkActive("sg-3");
        deleting.Delete(); // delete in flight: does not count
        var deleted = Rule(network, 4);
        deleted.MarkActive("sg-4");
        deleted.Delete();
        deleted.MarkDeleted(); // gone: does not count
        Rule(network, 5).MarkRequestFailed("reason", "detail"); // create failed before Incus had it: does not count
        return network;
    }

    [Fact]
    public void FirewallRuleQuotaUsage_CountsWhatIncusHoldsOrIsAboutTo_ButNotAFailedCreate_ADeleteInFlight_NorDeleted()
    {
        var network = NetworkWithEveryRuleSituation();

        Assert.Equal(5, network.FirewallRules.Count);
        Assert.Equal(2, network.FirewallRuleQuotaUsage);
    }

    [Fact]
    public void AddFirewallRule_OneBelowTheCap_AddsARuleWaitingForItsCreate()
    {
        var network = NetworkWithEveryRuleSituation(); // usage 2

        FirewallRule rule = network.AddFirewallRule(FirewallDirection.Egress, FirewallProtocol.UDP, PortRange.Of(53, 53), FirewallCidr.Parse("10.0.0.0/8")).Rule;

        Assert.Contains(rule, network.FirewallRules);
        Assert.Equal(network.Id, rule.NetworkId);
        Assert.Equal(TenantId, rule.TenantId);
        Assert.Equal(FirewallRuleTransition.Creating, rule.Transition);
        Assert.Equal(3, network.FirewallRuleQuotaUsage);
    }

    [Fact]
    public void AddFirewallRule_OnANetworkNotUsable_IsRefused()
    {
        var network = TestAggregates.Network(TenantIdOwner, "net-1", "cluster-1"); // Creating

        var ex = Assert.Throws<BusinessException>(
            () => network.AddFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, PortRange.Of(80, 80), FirewallCidr.Parse("0.0.0.0/0")));

        Assert.Equal("FIREWALL_NETWORK_NOT_READY", ex.Code);
        Assert.Empty(network.FirewallRules);
    }

    // The network's delete took its ACL and every rule in it (titan-ops features/network/design.md#network-lifecycle).
    // A rule's own waiting delete ends with it; which rules waited is read before the call, by NetworkReports.
    [Fact]
    public void DeleteDone_MarksEveryLiveRuleDeleted_AndEndsTheirWaitingOperations()
    {
        var network = UsableNetwork();
        var enabled = Rule(network, 22);
        enabled.MarkActive("sg-22");
        var deleting = Rule(network, 23);
        deleting.MarkActive("sg-23");
        deleting.Delete();
        var alreadyGone = Rule(network, 24);
        alreadyGone.MarkActive("sg-24");
        alreadyGone.Delete();
        alreadyGone.MarkDeleted();
        var creating = Rule(network, 25);
        network.Delete();

        PendingOperation<NetworkOperationKind>? outcome = network.MarkDeleted();

        Assert.Equal(NetworkOperationKind.Delete, outcome!.Kind);
        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.All(network.FirewallRules, r => Assert.Equal(CurrentExistence.Deleted, r.CurrentExistence));
        Assert.All(network.FirewallRules, r => Assert.Null(r.Pending));
        Assert.Null(creating.Transition);
    }

    [Fact]
    public void DeleteDone_WithNoRules_EndsTheDelete()
    {
        var network = UsableNetwork();
        network.Delete();

        PendingOperation<NetworkOperationKind>? outcome = network.MarkDeleted();

        Assert.Equal(NetworkOperationKind.Delete, outcome!.Kind);
        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
    }

    // Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs): a repeat ends nothing
    // and changes nothing.
    [Fact]
    public void DeleteDone_Repeated_IsIgnored()
    {
        var network = UsableNetwork();
        var deleting = Rule(network, 23);
        deleting.MarkActive("sg-23");
        deleting.Delete();
        Guid deleteId = network.Delete()!.OperationId;
        network.FinishOperation(deleteId, null, null);

        PendingOperation<NetworkOperationKind>? repeat = network.FinishOperation(deleteId, null, null);

        Assert.Null(repeat);
        Assert.Equal(CurrentExistence.Deleted, deleting.CurrentExistence);
    }

    // A rule the network does not have answers like a missing one (titan-ops features/network/design.md#firewall-rule-soft-delete).
    [Fact]
    public void ARuleOfAnotherNetwork_IsNotFound()
    {
        var network = UsableNetwork();
        var other = UsableNetwork();
        var foreign = Rule(other, 22);
        Rule(network, 23);

        Assert.DoesNotContain(network.FirewallRules, r => r.Id == foreign.Id);
        Assert.Equal("FIREWALL_RULE_NOT_FOUND", Assert.Throws<NotFoundException>(() => network.DeleteFirewallRule(foreign.Id)).Code);
        Assert.Equal("FIREWALL_RULE_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => network.FinishFirewallRuleOperation(foreign.Id, foreign.Pending!.Id, "sg-22", null)).Code);
        Assert.Equal("FIREWALL_RULE_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => network.MarkFirewallRuleObserved(foreign.Id, FirewallRuleState.enabled)).Code);
        Assert.Equal("FIREWALL_RULE_NOT_FOUND", Assert.Throws<NotFoundException>(() => network.MarkFirewallRuleNotFound(foreign.Id)).Code);
        Assert.Equal(FirewallRuleTransition.Creating, foreign.Transition);
    }

    // The root routes to the rule and returns what the rule returns: the same answers the rule gave when handlers
    // reached it directly.
    [Fact]
    public void FirewallRuleCalls_ReachTheRuleNamed_AndReturnItsAnswer()
    {
        var network = UsableNetwork();
        var rule = Rule(network, 22);
        var untouched = Rule(network, 23);

        Guid createId = rule.Pending!.Id;
        Assert.Equal(createId, network.FinishFirewallRuleOperation(rule.Id, createId, "sg-22", null)!.Id);
        network.MarkFirewallRuleObserved(rule.Id, FirewallRuleState.disabled);
        Assert.Equal(FirewallRuleState.disabled, rule.CurrentState);

        OperationTicket delete = network.DeleteFirewallRule(rule.Id)!;
        Assert.Equal("sg-22", delete.ProviderId);
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
        network.FailFirewallRuleOperation(rule.Id, delete.OperationId, null, null, "refused", "raw");
        Assert.Null(rule.Transition);
        Assert.Equal("refused", rule.OperationFailureReason);

        network.MarkFirewallRuleNotFound(rule.Id);
        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        OperationTicket again = network.DeleteFirewallRule(rule.Id)!;
        Assert.Equal(FirewallRuleOperationKind.Delete,
            network.FinishFirewallRuleOperation(rule.Id, again.OperationId, null, null)!.Kind);
        Assert.True(rule.IsTerminal);

        Assert.Equal(FirewallRuleTransition.Creating, untouched.Transition);
        Assert.Null(untouched.ExternalId);
    }

    [Fact]
    public void RequestFirewallRuleDelete_OfARuleWhoseCreateFailed_WritesBothPairs_WithNoTicket()
    {
        var network = UsableNetwork();
        var failed = Rule(network, 22);
        network.FailFirewallRuleOperation(failed.Id, failed.Pending!.Id, null, null, "reason", "detail");

        Assert.Null(network.DeleteFirewallRule(failed.Id));

        Assert.Equal(CurrentExistence.Deleted, failed.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, failed.DesiredExistence);
        Assert.Equal(0, network.FirewallRuleQuotaUsage);
    }
}
