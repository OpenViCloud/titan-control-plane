using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// One rule of a network: a line in the network's ACL on Incus (titan-ops features/network/design.md#firewall-on-incus).
/// A child of Network (titan-ops architecture/code-structure.md#child-entity): the network's delete taking
/// every rule with it is the network's invariant, so only the root reaches the methods below, and
/// handlers go through Network.AddFirewallRule, DeleteFirewallRule,
/// FirewallRuleCreateSucceeded, FirewallRuleCreateFailed, FirewallRuleDeleteSucceeded, FirewallRuleDeleteFailed and MarkFirewallRule*.
public partial class FirewallRule : IEntity
{
    public Guid Id { get; private set; }
    public Guid NetworkId { get; private set; }
    public Guid TenantId { get; private set; }
    public FirewallDirection Direction { get; private set; }
    public FirewallProtocol Protocol { get; private set; }
    public int? StartPort { get; private set; }
    public int? EndPort { get; private set; }
    public FirewallCidr Cidr { get; private set; } = default!;
    public string? ExternalId { get; private set; }
    // Same split as Network (titan-ops architecture/desired-and-current.md#desired-and-current): CurrentState is the Incus
    // ACL rule state, null while the rule is not in the ACL.
    public FirewallRuleState? CurrentState { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    /// The rule's own operation waiting on Incus, independent of its network's, and the last one that failed.
    public FirewallRuleOperation Operation { get; private set; } = FirewallRuleOperation.None;
    public DateTimeOffset CreatedAt { get; private set; }

    /// Deleted absorbs every later observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs):
    /// a late observation must not bring back a rule Incus has already removed. Until then the rule is still listed
    /// under its network and still observed, and one whose delete is waiting is still in force on Incus;
    /// FirewallRuleRepository's list and reconciler queries write !IsDeleted in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => CurrentExistence == CurrentExistence.Deleted;

    private FirewallRule() { }

    /// The ports as one value; null for ICMP and All. The two columns stay the storage: EF Core 9 has no nullable
    /// complex property, so the value is rebuilt from them (titan-ops architecture/code-structure.md#logic-placement).
    public PortRange? Ports
    {
        get
        {
            if (StartPort == null || EndPort == null)
            {
                return null;
            }
            return PortRange.Of(StartPort.Value, EndPort.Value);
        }
    }

    /// The move the waiting operation makes, for display.
    public FirewallRuleTransition? Transition
    {
        get
        {
            switch (Operation.Kind)
            {
                case FirewallRuleOperationKind.Create:
                    return FirewallRuleTransition.Creating;
                case FirewallRuleOperationKind.Delete:
                    return FirewallRuleTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => Operation.Kind == FirewallRuleOperationKind.Create;
    private bool DeleteIsWaiting => Operation.Kind == FirewallRuleOperationKind.Delete;
    /// A delete was asked and still waits, or the rule is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsDeleted;

    /// Counted by occupancy toward MaxFirewallRulesPerNetwork (titan-ops features/network/design.md#firewall-rule-quota-counting):
    /// what Incus holds or is about to hold. A delete asked for stops counting at once; a failed create with nothing
    /// on Incus, and a rule Incus lost, do not count.
    public bool CountsTowardQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && CreateIsWaiting));

    // ---- The report of the rule's own command (titan-ops features/network/design.md#network-operation): the one thing that
    // ends its operation; CreateSucceeded and CreateFailed are in FirewallRule.Creation.cs, DeleteSucceeded and DeleteFailed
    // in FirewallRule.Deletion.cs.

    /// The report names the operation of this kind the rule waits on. A rule already Deleted waits on nothing.
    private bool IsWaitingOn(Guid operationId, FirewallRuleOperationKind kind) =>
        !IsDeleted && Operation.PendingId == operationId && Operation.Kind == kind;

    /// A Done must leave the rule named on Incus, or nothing would be left to delete it through. Without a stored id the
    /// waiting operation can only be the one that makes it. Checked before the report writes anything; a worker that
    /// sends it has a bug, and does not retry a 422.
    private void EnsureTheDoneLeavesAnId(string? reportedId)
    {
        bool doneWithoutAnId = ExternalId == null && string.IsNullOrEmpty(reportedId);
        if (doneWithoutAnId)
        {
            string operation = Operation.Kind!.Value.ToString().ToLowerInvariant();
            throw new BusinessException("INVALID_STATUS", $"ExternalId is required when a {operation} ends Done.");
        }
    }

    private void RecordWhatTheWorkerSaw(string? externalId, FirewallRuleState? observedState)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
            CurrentExistence = CurrentExistence.Exists;
        }
        if (observedState != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentState = observedState;
        }
    }

    /// The reconciler lists only rules with an ExternalId (FirewallRuleRepository.ListReconcilableAsync), so this
    /// cannot fire on that path; it throws rather than let a caller assert the id.
    public string GetExternalId()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("FIREWALL_RULE_INVALID_STATE", "Firewall rule has no provider resource yet.");
        }
        return ExternalId;
    }
}
