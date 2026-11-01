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
        deleting.RequestDelete(); // delete in flight: does not count
        var deleted = Rule(network, 4);
        deleted.MarkActive("sg-4");
        deleted.RequestDelete();
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
    // Only a rule whose own delete was waiting is named: its request is done here, once.
    [Fact]
    public void MarkDeleted_MarksEveryLiveRuleDeleted_AndNamesOnlyTheRulesWhoseOwnDeleteWasWaiting()
    {
        var network = UsableNetwork();
        var enabled = Rule(network, 22);
        enabled.MarkActive("sg-22");
        var deleting = Rule(network, 23);
        deleting.MarkActive("sg-23");
        deleting.RequestDelete();
        var alreadyGone = Rule(network, 24);
        alreadyGone.MarkActive("sg-24");
        alreadyGone.RequestDelete();
        alreadyGone.MarkDeleted();
        network.RequestDelete();

        NetworkDeletionOutcome outcome = network.MarkDeleted();

        Assert.True(outcome.FulfilledNetworkDelete);
        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.All(network.FirewallRules, r => Assert.Equal(CurrentExistence.Deleted, r.CurrentExistence));
        Assert.Equal([deleting.Id], outcome.RulesWhoseDeleteWasFulfilled.Select(r => r.Id));
    }

    [Fact]
    public void MarkDeleted_WithNoRules_NamesNone()
    {
        var network = UsableNetwork();
        network.RequestDelete();

        NetworkDeletionOutcome outcome = network.MarkDeleted();

        Assert.True(outcome.FulfilledNetworkDelete);
        Assert.Empty(outcome.RulesWhoseDeleteWasFulfilled);
    }

    // Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs): a repeat changes nothing
    // and names nothing again.
    [Fact]
    public void MarkDeleted_Repeated_IsAbsorbed_AndNamesNothingAgain()
    {
        var network = UsableNetwork();
        var deleting = Rule(network, 23);
        deleting.MarkActive("sg-23");
        deleting.RequestDelete();
        network.RequestDelete();
        network.MarkDeleted();

        NetworkDeletionOutcome repeat = network.MarkDeleted();

        Assert.False(repeat.FulfilledNetworkDelete);
        Assert.Empty(repeat.RulesWhoseDeleteWasFulfilled);
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
        var ex = Assert.Throws<NotFoundException>(() => network.DeleteFirewallRuleLocally(foreign.Id));
        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
        Assert.Equal("FIREWALL_RULE_NOT_FOUND", Assert.Throws<NotFoundException>(() => network.RequestFirewallRuleDelete(foreign.Id)).Code);
        Assert.Equal("FIREWALL_RULE_NOT_FOUND", Assert.Throws<NotFoundException>(() => network.MarkFirewallRuleDeleted(foreign.Id)).Code);
    }

    // The root routes to the rule and returns what the rule returns: the same answers the rule gave when handlers
    // reached it directly.
    [Fact]
    public void FirewallRuleCalls_ReachTheRuleNamed_AndReturnItsAnswer()
    {
        var network = UsableNetwork();
        var rule = Rule(network, 22);
        var untouched = Rule(network, 23);

        network.MarkFirewallRuleCreated(rule.Id, "sg-22");
        Assert.True(network.MarkFirewallRuleObserved(rule.Id, FirewallRuleState.enabled));
        Assert.False(network.MarkFirewallRuleObserved(rule.Id, FirewallRuleState.disabled));
        Assert.Equal(FirewallRuleState.disabled, rule.CurrentState);

        Assert.Equal("sg-22", network.RequestFirewallRuleDelete(rule.Id));
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
        network.AbandonFirewallRuleRequest(rule.Id, "refused", "raw");
        Assert.Null(rule.Transition);
        Assert.Equal("refused", rule.RequestFailureReason);

        network.MarkFirewallRuleNotFound(rule.Id);
        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        network.RequestFirewallRuleDelete(rule.Id);
        Assert.True(network.MarkFirewallRuleDeleted(rule.Id));

        Assert.Equal(FirewallRuleTransition.Creating, untouched.Transition);
        Assert.Null(untouched.ExternalId);
    }

    [Fact]
    public void DeleteFirewallRuleLocally_OnARuleWhoseCreateFailed_WritesBothPairs()
    {
        var network = UsableNetwork();
        var failed = Rule(network, 22);
        network.MarkFirewallRuleRequestFailed(failed.Id, "reason", "detail");

        network.DeleteFirewallRuleLocally(failed.Id);

        Assert.Equal(CurrentExistence.Deleted, failed.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, failed.DesiredExistence);
        Assert.Equal(0, network.FirewallRuleQuotaUsage);
    }
}
