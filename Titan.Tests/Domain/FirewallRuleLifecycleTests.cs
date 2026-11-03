using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// A rule's state is the Incus ACL rule state word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim),
// with the existence axis beside it (titan-ops features/network/design.md#firewall-rule-status).
public class FirewallRuleLifecycleTests
{
    [Fact]
    public void Create_HasNoIncusState_IsNotCreated_AndWaitsForItsCreate()
    {
        var rule = NewRule();

        Assert.Null(rule.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, rule.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Equal(FirewallRuleTransition.Creating, rule.Transition);
    }

    [Fact]
    public void MarkCreated_RecordsTheIdAndThatIncusHasIt_EndsTheCreate_KeepingTheFirstId_AndLeavesTheStateToTheReconciler()
    {
        var rule = NewRule();

        rule.MarkCreated("acl-n/rule-1");
        rule.MarkCreated("acl-n/other");

        Assert.Equal("acl-n/rule-1", rule.ExternalId);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
        Assert.Null(rule.Transition);
    }

    // Any word Incus holds for the rule is recorded as it is: disabled or logged set outside Titan is shown.
    [Theory]
    [InlineData(FirewallRuleState.enabled)]
    [InlineData(FirewallRuleState.disabled)]
    [InlineData(FirewallRuleState.logged)]
    public void MarkObserved_RecordsTheIncusWord(FirewallRuleState state)
    {
        var rule = ActiveRule();

        rule.MarkObserved(state);

        Assert.Equal(state, rule.CurrentState);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
    }

    [Fact]
    public void Observation_WhileADeleteWaits_DoesNotUndoTheDelete()
    {
        var rule = ActiveRule();
        rule.RequestDelete();

        rule.MarkObserved(FirewallRuleState.enabled);

        Assert.Equal(DesiredExistence.Deleted, rule.DesiredExistence);
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
    }

    // ── Not found: the reconciler never reports Deleted (titan-ops architecture/desired-and-current.md#existence-axis) ──

    [Fact]
    public void NotFound_ClearsTheState_KeepsTheId()
    {
        var rule = ActiveRule();

        rule.MarkNotFound();

        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
        Assert.Equal("acl-n/rule-1", rule.ExternalId);
    }

    [Fact]
    public void NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var rule = ActiveRule();
        rule.RequestDelete();

        rule.MarkNotFound();

        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
        Assert.True(rule.MarkDeleted());
    }

    [Fact]
    public void SeenAgainAfterNotFound_Exists()
    {
        var rule = ActiveRule();
        rule.MarkNotFound();

        rule.MarkObserved(FirewallRuleState.enabled);

        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
    }

    // ── Request failure (titan-ops architecture/desired-and-current.md#request-failure) ──

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsWhy_AndEndsTheCreate()
    {
        var rule = NewRule();

        rule.MarkRequestFailed("Firewall rule creation failed.", "raw");

        Assert.Equal("Firewall rule creation failed.", rule.RequestFailureReason);
        Assert.Equal("raw", rule.RequestFailureDetail);
        Assert.NotNull(rule.RequestFailedAt);
        Assert.Null(rule.Transition);
        Assert.Equal(CurrentExistence.NotCreated, rule.CurrentExistence);
    }

    [Fact]
    public void RequestFailed_WithNoCreateWaiting_IsStale()
    {
        var rule = ActiveRule();

        rule.MarkRequestFailed("late", "late");

        Assert.Null(rule.RequestFailureReason);
    }

    // ── RequestDelete ──

    [Fact]
    public void RequestDelete_WritesDesiredExistence_AndYieldsTheId()
    {
        var rule = ActiveRule();

        Assert.Equal("acl-n/rule-1", rule.RequestDelete().ProviderId);
        Assert.Equal(DesiredExistence.Deleted, rule.DesiredExistence);
        Assert.Equal(FirewallRuleState.enabled, rule.CurrentState);
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
    }

    [Theory]
    [InlineData(FirewallRuleState.disabled)]
    [InlineData(FirewallRuleState.logged)]
    public void RequestDelete_IsAccepted_WhateverItsState(FirewallRuleState state)
    {
        var rule = ActiveRule();
        rule.MarkObserved(state);

        Assert.Equal("acl-n/rule-1", rule.RequestDelete().ProviderId);
    }

    [Fact]
    public void RequestDelete_OfARuleIncusNoLongerHas_GoesThroughIncus()
    {
        var rule = ActiveRule();
        rule.MarkNotFound();

        Assert.Equal("acl-n/rule-1", rule.RequestDelete().ProviderId);
    }

    [Fact]
    public void RequestDelete_WhileTheCreateWaits_IsNotReady()
    {
        var rule = NewRule();

        Assert.Equal("FIREWALL_RULE_NOT_READY", Assert.Throws<BusinessException>(() => rule.RequestDelete()).Code);
    }

    [Fact]
    public void RequestDelete_Repeated_OrOfADeletedRule_OrWithNoId_IsRefused()
    {
        var waiting = ActiveRule();
        waiting.RequestDelete();
        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(() => waiting.RequestDelete()).Code);

        var deleted = ActiveRule();
        deleted.RequestDelete();
        deleted.MarkDeleted();
        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(() => deleted.RequestDelete()).Code);

        var failed = NewRule();
        failed.MarkRequestFailed("r", "d");
        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(() => failed.RequestDelete()).Code);
    }

    // ── Deleted ──

    [Fact]
    public void MarkDeleted_OnAWaitingDelete_EndsIt_Once_AndIsTerminal()
    {
        var rule = ActiveRule();
        rule.RequestDelete();

        Assert.True(rule.MarkDeleted());
        Assert.False(rule.MarkDeleted());
        Assert.True(rule.IsTerminal);
        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
    }

    // The network's delete takes its ACL, and the rule with it, whatever the rule waited on; only the rule's own delete
    // is named, so its outcome is recorded once.
    [Fact]
    public void EndWithItsNetwork_WithNoDeleteOfItsOwn_NamesNothing_ButIsTerminal()
    {
        var rule = ActiveRule();

        Assert.False(rule.EndWithItsNetwork());
        Assert.True(rule.IsTerminal);
        Assert.Null(rule.Pending);
    }

    [Fact]
    public void EndWithItsNetwork_WhileItsOwnDeleteWaits_SaysSo_Once()
    {
        var rule = ActiveRule();
        rule.RequestDelete();

        Assert.True(rule.EndWithItsNetwork());
        Assert.False(rule.EndWithItsNetwork());
        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Null(rule.Transition);
    }

    [Fact]
    public void EndWithItsNetwork_WhileItsCreateWaits_EndsTheCreate_AndIsTerminal()
    {
        var rule = NewRule();

        Assert.False(rule.EndWithItsNetwork());
        Assert.True(rule.IsTerminal);
        Assert.Null(rule.Transition);
    }

    [Fact]
    public void Deleted_AbsorbsLaterObservations()
    {
        var rule = ActiveRule();
        rule.RequestDelete();
        rule.MarkDeleted();

        rule.MarkObserved(FirewallRuleState.enabled);
        rule.MarkNotFound();
        rule.MarkCreated("acl-n/other");
        rule.MarkRequestFailed("late", "late");

        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
        Assert.Equal("acl-n/rule-1", rule.ExternalId);
    }

    // ── DeleteLocally ──

    [Fact]
    public void DeleteLocally_OfAFailedCreateWithNoId_EndsDeleted()
    {
        var rule = NewRule();
        rule.MarkRequestFailed("r", "d");

        rule.DeleteLocally();

        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, rule.DesiredExistence);
    }

    [Fact]
    public void DeleteLocally_OnARuleIncusMayHold_OrWhileItsCreateWaits_IsRefused()
    {
        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(ActiveRule().DeleteLocally).Code);
        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(NewRule().DeleteLocally).Code);
    }

    private static FirewallRule NewRule() =>
        FirewallRule.Create(Guid.NewGuid(), Guid.NewGuid(), FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "10.0.0.0/24");

    private static FirewallRule ActiveRule()
    {
        var rule = NewRule();
        rule.MarkActive("acl-n/rule-1");
        return rule;
    }
}
