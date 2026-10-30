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
    public void MarkCreated_RecordsTheIdAndThatIncusHasIt_KeepingTheFirstId_AndLeavesTheStateToTheReconciler()
    {
        var rule = NewRule();

        rule.MarkCreated("acl-n/rule-1");
        rule.MarkCreated("acl-n/other");

        Assert.Equal("acl-n/rule-1", rule.ExternalId);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
        Assert.Equal(FirewallRuleTransition.Creating, rule.Transition);
    }

    [Fact]
    public void FirstObservation_FulfilsTheCreateOnce()
    {
        var rule = NewRule();
        rule.MarkCreated("acl-n/rule-1");

        bool first = rule.MarkObserved(FirewallRuleState.enabled);
        bool repeat = rule.MarkObserved(FirewallRuleState.enabled);

        Assert.True(first);
        Assert.False(repeat);
        Assert.Equal(FirewallRuleState.enabled, rule.CurrentState);
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

        bool fulfilled = rule.MarkObserved(FirewallRuleState.enabled);

        Assert.False(fulfilled);
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
    public void NotFound_EndsAWaitingCreate()
    {
        var rule = NewRule();
        rule.MarkCreated("acl-n/rule-1");

        rule.MarkNotFound();

        Assert.Null(rule.Transition);
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

    [Fact]
    public void AbandonRequest_OnAWaitingDelete_GivesTheRuleBack_WithTheReason_AndTheNextDeleteTriesAgain()
    {
        var rule = ActiveRule();
        rule.RequestDelete();

        rule.AbandonRequest("Firewall rule deletion failed.", "raw");

        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Null(rule.Transition);
        Assert.Equal("Firewall rule deletion failed.", rule.RequestFailureReason);
        Assert.Equal("acl-n/rule-1", rule.RequestDelete());
    }

    [Fact]
    public void AbandonRequest_WithNoDeleteWaiting_IsRefused()
    {
        var rule = ActiveRule();

        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(() => rule.AbandonRequest("r", "d")).Code);
    }

    // ── RequestDelete ──

    [Fact]
    public void RequestDelete_WritesDesiredExistence_AndYieldsTheId()
    {
        var rule = ActiveRule();

        Assert.Equal("acl-n/rule-1", rule.RequestDelete());
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

        Assert.Equal("acl-n/rule-1", rule.RequestDelete());
    }

    [Fact]
    public void RequestDelete_OfARuleIncusNoLongerHas_GoesThroughIncus()
    {
        var rule = ActiveRule();
        rule.MarkNotFound();

        Assert.Equal("acl-n/rule-1", rule.RequestDelete());
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
    public void MarkDeleted_OnAWaitingDelete_FulfilsIt_AndIsTerminal()
    {
        var rule = ActiveRule();
        rule.RequestDelete();

        Assert.True(rule.MarkDeleted());
        Assert.False(rule.MarkDeleted());
        Assert.True(rule.IsTerminal);
        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
    }

    // The network's delete takes its ACL, and the rule with it: Deleted with no delete of its own asked fulfils nothing.
    [Fact]
    public void MarkDeleted_WithNoDeleteOfItsOwn_FulfilsNothing_ButIsTerminal()
    {
        var rule = ActiveRule();

        Assert.False(rule.MarkDeleted());
        Assert.True(rule.IsTerminal);
    }

    [Fact]
    public void Deleted_AbsorbsLaterObservations()
    {
        var rule = ActiveRule();
        rule.RequestDelete();
        rule.MarkDeleted();

        Assert.False(rule.MarkObserved(FirewallRuleState.enabled));
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
        FirewallRule.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "10.0.0.0/24");

    private static FirewallRule ActiveRule()
    {
        var rule = NewRule();
        rule.MarkActive("acl-n/rule-1");
        return rule;
    }
}
