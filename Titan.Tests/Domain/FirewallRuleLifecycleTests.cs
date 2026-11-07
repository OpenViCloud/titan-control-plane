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

        Assert.Null(rule.InstanceStatus);
        Assert.True(rule.Status == FirewallRuleStatus.Creating || rule.Status == FirewallRuleStatus.Failed);
        Assert.False(rule.IsDeletedOrDeleting);
        Assert.Equal(FirewallRuleStatus.Creating, rule.Status);
    }

    [Fact]
    public void MarkCreated_RecordsTheIdAndThatIncusHasIt_EndsTheCreate_KeepingTheFirstId_AndLeavesTheStateToTheReconciler()
    {
        var rule = NewRule();

        rule.MarkCreated("acl-n/rule-1");
        rule.MarkCreated("acl-n/other");

        Assert.Equal("acl-n/rule-1", rule.ExternalId);
        Assert.Equal(FirewallRuleStatus.Unknown, rule.Status);
        Assert.Null(rule.InstanceStatus);
    }

    // Any word Incus holds for the rule is recorded as it is: disabled or logged set outside Titan is shown.
    [Theory]
    [InlineData(FirewallRuleStatus.enabled)]
    [InlineData(FirewallRuleStatus.disabled)]
    [InlineData(FirewallRuleStatus.logged)]
    public void MarkObserved_RecordsTheIncusWord(FirewallRuleStatus state)
    {
        var rule = ActiveRule();

        rule.MarkObserved(state);

        Assert.Equal(state, rule.Status);
        Assert.True(FirewallRule.IsIncusStatus(rule.Status));
    }

    [Fact]
    public void Observation_WhileADeleteWaits_DoesNotUndoTheDelete()
    {
        var rule = ActiveRule();
        rule.Delete();

        rule.MarkObserved(FirewallRuleStatus.enabled);

        Assert.True(rule.IsDeletedOrDeleting);
        Assert.Equal(FirewallRuleStatus.Deleting, rule.Status);
    }

    // ── Not found: the reconciler never reports Deleted (titan-ops architecture/desired-and-current.md#existence-axis) ──

    [Fact]
    public void NotFound_ClearsTheState_KeepsTheId()
    {
        var rule = ActiveRule();

        rule.MarkNotFound();

        Assert.Equal(FirewallRuleStatus.NotFound, rule.Status);
        Assert.Null(rule.InstanceStatus);
        Assert.Equal("acl-n/rule-1", rule.ExternalId);
    }

    [Fact]
    public void NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var rule = ActiveRule();
        rule.Delete();

        rule.MarkNotFound();

        Assert.Equal(FirewallRuleStatus.Deleting, rule.Status);
        Assert.True(rule.MarkDeleted());
    }

    [Fact]
    public void SeenAgainAfterNotFound_Exists()
    {
        var rule = ActiveRule();
        rule.MarkNotFound();

        rule.MarkObserved(FirewallRuleStatus.enabled);

        Assert.True(FirewallRule.IsIncusStatus(rule.Status));
    }

    // ── Request failure (titan-ops architecture/desired-and-current.md#request-failure) ──

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsWhy_AndEndsTheCreate()
    {
        var rule = NewRule();

        rule.MarkRequestFailed("Firewall rule creation failed.", "raw");

        Assert.Equal("Firewall rule creation failed.", rule.Operation.FailureReason);
        Assert.Equal("raw", rule.Operation.FailureDetail);
        Assert.NotNull(rule.Operation.FailedAt);
        Assert.True(rule.Status != FirewallRuleStatus.Creating && rule.Status != FirewallRuleStatus.Deleting);
        Assert.True(rule.Status == FirewallRuleStatus.Creating || rule.Status == FirewallRuleStatus.Failed);
    }

    [Fact]
    public void RequestFailed_WithNoCreateWaiting_IsStale()
    {
        var rule = ActiveRule();

        rule.MarkRequestFailed("late", "late");

        Assert.Null(rule.Operation.FailureReason);
    }

    // ── Delete ──

    [Fact]
    public void RequestDelete_WritesDesiredExistence_AndYieldsTheId()
    {
        var rule = ActiveRule();

        Assert.Equal("acl-n/rule-1", rule.Delete()!.ProviderId);
        Assert.True(rule.IsDeletedOrDeleting);
        Assert.Equal(FirewallRuleStatus.enabled, rule.InstanceStatus);
        Assert.Equal(FirewallRuleStatus.Deleting, rule.Status);
    }

    [Theory]
    [InlineData(FirewallRuleStatus.disabled)]
    [InlineData(FirewallRuleStatus.logged)]
    public void RequestDelete_IsAccepted_WhateverItsState(FirewallRuleStatus state)
    {
        var rule = ActiveRule();
        rule.MarkObserved(state);

        Assert.Equal("acl-n/rule-1", rule.Delete()!.ProviderId);
    }

    [Fact]
    public void RequestDelete_OfARuleIncusNoLongerHas_GoesThroughIncus()
    {
        var rule = ActiveRule();
        rule.MarkNotFound();

        Assert.Equal("acl-n/rule-1", rule.Delete()!.ProviderId);
    }

    [Fact]
    public void RequestDelete_WhileTheCreateWaits_IsNotReady()
    {
        var rule = NewRule();

        Assert.Equal("FIREWALL_RULE_NOT_READY", Assert.Throws<BusinessException>(() => rule.Delete()).Code);
    }

    [Fact]
    public void RequestDelete_Repeated_OrOfADeletedRule_IsRefused()
    {
        var waiting = ActiveRule();
        waiting.Delete();
        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(() => waiting.Delete()).Code);

        var deleted = ActiveRule();
        deleted.Delete();
        deleted.MarkDeleted();
        Assert.Equal("FIREWALL_RULE_INVALID_STATE", Assert.Throws<BusinessException>(() => deleted.Delete()).Code);
    }

    // ── Deleted ──

    [Fact]
    public void MarkDeleted_OnAWaitingDelete_EndsIt_Once_AndIsDeleted()
    {
        var rule = ActiveRule();
        rule.Delete();

        Assert.True(rule.MarkDeleted());
        Assert.False(rule.MarkDeleted());
        Assert.True(rule.IsDeleted);
        Assert.Equal(FirewallRuleStatus.Deleted, rule.Status);
        Assert.Null(rule.InstanceStatus);
    }

    // The network's delete takes its ACL, and the rule with it, whatever the rule waited on.
    [Fact]
    public void EndWithItsNetwork_WithNoDeleteOfItsOwn_IsDeleted()
    {
        var rule = ActiveRule();

        rule.EndWithItsNetwork();

        Assert.True(rule.IsDeleted);
        Assert.Null(rule.Operation.PendingId);
    }

    [Fact]
    public void EndWithItsNetwork_WhileItsOwnDeleteWaits_EndsIt_AndARepeatChangesNothing()
    {
        var rule = ActiveRule();
        rule.Delete();

        rule.EndWithItsNetwork();
        rule.EndWithItsNetwork();

        Assert.Equal(FirewallRuleStatus.Deleted, rule.Status);
        Assert.Null(rule.Operation.PendingId);
        Assert.True(rule.Status != FirewallRuleStatus.Creating && rule.Status != FirewallRuleStatus.Deleting);
    }

    [Fact]
    public void EndWithItsNetwork_WhileItsCreateWaits_EndsTheCreate_AndIsDeleted()
    {
        var rule = NewRule();

        rule.EndWithItsNetwork();

        Assert.True(rule.IsDeleted);
        Assert.True(rule.Status != FirewallRuleStatus.Creating && rule.Status != FirewallRuleStatus.Deleting);
    }

    [Fact]
    public void Deleted_AbsorbsLaterObservations()
    {
        var rule = ActiveRule();
        rule.Delete();
        rule.MarkDeleted();

        rule.MarkObserved(FirewallRuleStatus.enabled);
        rule.MarkNotFound();
        rule.MarkCreated("acl-n/other");
        rule.MarkRequestFailed("late", "late");

        Assert.Equal(FirewallRuleStatus.Deleted, rule.Status);
        Assert.Null(rule.InstanceStatus);
        Assert.Equal("acl-n/rule-1", rule.ExternalId);
    }

    // ── Delete of a rule that never reached Incus ──

    [Fact]
    public void RequestDelete_OfAFailedCreateWithNoId_EndsDeleted_WithNoTicket()
    {
        var rule = NewRule();
        rule.MarkRequestFailed("r", "d");

        Assert.Null(rule.Delete());

        Assert.Equal(FirewallRuleStatus.Deleted, rule.Status);
        Assert.True(rule.IsDeletedOrDeleting);
    }

    [Fact]
    public void RequestDelete_OfAFailedCreateThatSawAState_WithNoId_LeavesNoStateBehind()
    {
        var rule = NewRule();
        rule.ReportFailed(rule.Operation.PendingId!.Value, null, FirewallRuleStatus.enabled, "r", "d");
        Assert.Equal(FirewallRuleStatus.Failed, rule.Status);
        Assert.Null(rule.InstanceStatus);

        Assert.Null(rule.Delete());

        Assert.Equal(FirewallRuleStatus.Deleted, rule.Status);
        Assert.Null(rule.InstanceStatus);
    }

    [Fact]
    public void RequestDelete_OfARuleIncusMayHold_OrWhileItsCreateWaits_DoesNotDeleteHere()
    {
        FirewallRule active = ActiveRule();
        FirewallRule creating = NewRule();
        Assert.False(LocalDelete.DeletedHere(() => active.Delete()));
        Assert.False(LocalDelete.DeletedHere(() => creating.Delete()));
    }

    private static FirewallRule NewRule() =>
        FirewallRule.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "10.0.0.0/24");

    private static FirewallRule ActiveRule()
    {
        var rule = NewRule();
        rule.MarkActive("acl-n/rule-1");
        return rule;
    }

    [Fact]
    public void AFailedRule_IgnoresEveryObservation()
    {
        var rule = NewRule();
        rule.MarkRequestFailed("reason", "raw");

        rule.MarkObserved(FirewallRuleStatus.enabled);
        rule.MarkNotFound();

        Assert.Equal(FirewallRuleStatus.Failed, rule.Status);
        Assert.Null(rule.InstanceStatus);
        Assert.False(rule.CountsTowardQuota);
    }
}
