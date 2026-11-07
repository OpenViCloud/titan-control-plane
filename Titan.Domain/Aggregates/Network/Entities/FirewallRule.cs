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
    /// The one status the API shows (titan-ops features/network/design.md#firewall-rule-status). Only an operation, a report
    /// ending it, or an observation at rest writes it, each through the moves of FirewallRule.Creation.cs,
    /// FirewallRule.Deletion.cs and FirewallRule.Observation.cs.
    public FirewallRuleStatus Status { get; private set; }
    /// The rule's own operation waiting on Incus, independent of its network's, the last one that failed, and the Incus state
    /// seen while one waits.
    public FirewallRuleOperation Operation { get; private set; } = FirewallRuleOperation.None;

    /// The Incus state the rule holds as far as Titan last knew, null when none is known: the state seen while an operation
    /// waits, else Status when it is one of the three Incus states (IsIncusStatus). Worked out, never stored, and not on /v1.
    public FirewallRuleStatus? InstanceStatus
    {
        get
        {
            if (Operation.LastSeenStatus != null)
            {
                return Operation.LastSeenStatus;
            }
            if (IsIncusStatus(Status))
            {
                return Status;
            }
            return null;
        }
    }

    /// The three states Incus gives an ACL rule: the only ones a worker reports, and the only ones InstanceStatus can be.
    public static bool IsIncusStatus(FirewallRuleStatus status) =>
        status == FirewallRuleStatus.enabled
        || status == FirewallRuleStatus.disabled
        || status == FirewallRuleStatus.logged;

    /// A worker reports only the three Incus states; any other status in a report is the worker's bug. No state at all (null)
    /// is a report that read none, not a foreign one.
    private static bool IsForeignStatus(FirewallRuleStatus? status) => status != null && !IsIncusStatus(status.Value);

    private static void EnsureNotForeign(FirewallRuleStatus? status)
    {
        if (IsForeignStatus(status))
        {
            throw new BusinessException("INVALID_STATUS", $"'{status}' is not an Incus firewall rule state.");
        }
    }

    /// An operation waits on its command: only the report carrying its id moves the status on.
    private bool IsTransitionStatus => Status == FirewallRuleStatus.Creating || Status == FirewallRuleStatus.Deleting;
    public DateTimeOffset CreatedAt { get; private set; }

    /// Deleted absorbs every later observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs):
    /// a late observation must not bring back a rule Incus has already removed. Until then the rule is still listed
    /// under its network and still observed, and one whose delete is waiting is still in force on Incus;
    /// FirewallRuleRepository's list and reconciler queries write !IsDeleted in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => Status == FirewallRuleStatus.Deleted;

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

    private bool CreateIsWaiting => Status == FirewallRuleStatus.Creating;
    private bool DeleteIsWaiting => Status == FirewallRuleStatus.Deleting;
    /// A delete was asked and still waits, or the rule is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsDeleted;

    /// Counted by occupancy toward MaxFirewallRulesPerNetwork (titan-ops features/network/design.md#firewall-rule-quota-counting):
    /// what Incus holds or is about to hold. A delete asked for stops counting at once; a failed create with nothing
    /// on Incus, and a rule Incus lost, do not count.
    public bool CountsTowardQuota =>
        Status != FirewallRuleStatus.Deleting
        && Status != FirewallRuleStatus.Deleted
        && Status != FirewallRuleStatus.NotFound
        && Status != FirewallRuleStatus.Failed;

    // ---- The report of the rule's own command (titan-ops features/network/design.md#network-operation): the one thing that
    // ends its operation; CreateSucceeded and CreateFailed are in FirewallRule.Creation.cs, DeleteSucceeded and DeleteFailed
    // in FirewallRule.Deletion.cs.

    /// The report names the operation the rule waits on, which its status says.
    private bool IsWaitingOn(Guid operationId, FirewallRuleStatus waiting) =>
        Status == waiting && Operation.PendingId == operationId;

    /// A Done must leave the rule named on Incus, or nothing would be left to delete it through. Without a stored id the
    /// waiting operation can only be the one that makes it. Checked before the report writes anything; a worker that
    /// sends it has a bug, and does not retry a 422.
    private void EnsureTheDoneLeavesAnId(string? reportedId)
    {
        bool doneWithoutAnId = ExternalId == null && string.IsNullOrEmpty(reportedId);
        if (doneWithoutAnId)
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }
    }

    /// A report brings the provider id with it, once.
    private void RecordTheId(string? externalId)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
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
