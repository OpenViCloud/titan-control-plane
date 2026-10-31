using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// One rule of a network: a line in the network's ACL on Incus (titan-ops features/network/design.md#firewall-on-incus).
/// A child of Network (titan-ops architecture/code-structure.md#child-entity): the per-network cap and the network's
/// delete taking every rule with it are the network's invariants, so only the root reaches the methods below, and
/// handlers go through Network.AddFirewallRule, RequestFirewallRuleDelete and MarkFirewallRule*.
public class FirewallRule : ChildEntity
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
    public Guid? PendingIntentId { get; private set; }
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// Deleted absorbs every later observation (titan-ops architecture/intent-and-observation.md#intent-versus-observation):
    /// a late observation must not bring back a rule Incus has already removed.
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

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

    /// Derived, never stored.
    public FirewallRuleTransition? Transition
    {
        get
        {
            bool nothingIsWaiting = PendingIntentId == null;
            if (nothingIsWaiting)
            {
                return null;
            }
            if (DesiredExistence == DesiredExistence.Deleted)
            {
                return FirewallRuleTransition.Deleting;
            }
            return FirewallRuleTransition.Creating;
        }
    }

    private bool CreateIsWaiting => PendingIntentId != null && DesiredExistence == DesiredExistence.Exists;
    private bool DeleteIsWaiting => PendingIntentId != null && DesiredExistence == DesiredExistence.Deleted;

    /// Counted by occupancy toward MaxFirewallRulesPerNetwork (titan-ops features/network/design.md#firewall-rule-quota-counting):
    /// what Incus holds or is about to hold. A delete asked for stops counting at once; a failed create with nothing
    /// on Incus, and a rule Incus lost, do not count.
    public bool CountsTowardQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && PendingIntentId != null));

    /// Called by Network.AddFirewallRule after the network checked itself and the tenant's cap. Also the test
    /// seam: `internal`, reachable only from Titan.Tests (titan-ops architecture/code-structure.md#tenant-typed-edge).
    internal static FirewallRule CreateUnchecked(
        Guid networkId, Guid tenantId,
        FirewallDirection direction, FirewallProtocol protocol,
        int? startPort, int? endPort, string cidr)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid(),
            NetworkId = networkId,
            TenantId = tenantId,
            Direction = direction,
            Protocol = protocol,
            StartPort = startPort,
            EndPort = endPort,
            Cidr = FirewallCidr.Parse(cidr),
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            PendingIntentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): converge, never throw.

    /// The create command wrote the rule into its network's ACL. The first id heard is kept; the state is left to
    /// the reconciler.
    internal void MarkCreated(string externalId)
    {
        if (IsTerminal)
        {
            return;
        }

        bool firstProviderId = ExternalId == null;
        if (firstProviderId)
        {
            ExternalId = externalId;
        }
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler read the rule in its ACL. Returns true when this fulfilled the waiting create, so the
    /// callback records "firewall_rule.created" once. A waiting delete is never withdrawn by an observation.
    internal bool MarkObserved(FirewallRuleState state)
    {
        if (IsTerminal)
        {
            return false;
        }

        CurrentExistence = CurrentExistence.Exists;
        CurrentState = state;
        if (!CreateIsWaiting)
        {
            return false;
        }
        PendingIntentId = null;
        return true;
    }

    /// The reconciler did not find the rule in its ACL. Never Deleted: only a delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis). Ends a waiting create; a waiting delete keeps waiting.
    internal void MarkNotFound()
    {
        if (IsTerminal)
        {
            return;
        }

        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
        if (CreateIsWaiting)
        {
            PendingIntentId = null;
        }
    }

    /// The rule's delete command confirmed it gone, or its network's delete took the ACL with it. Returns true when
    /// this fulfilled the rule's own waiting delete.
    internal bool MarkDeleted()
    {
        if (IsTerminal)
        {
            return false;
        }

        bool fulfilled = DeleteIsWaiting;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        PendingIntentId = null;
        return fulfilled;
    }

    /// The create command gave up (titan-ops architecture/desired-and-current.md#request-failure): records why and ends
    /// the waiting create. A give-up with no create waiting is stale; a delete's give-up is AbandonRequest.
    internal void MarkRequestFailed(string? reason, string? detail)
    {
        if (IsTerminal || !CreateIsWaiting)
        {
            return;
        }

        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
        PendingIntentId = null;
    }

    /// Intent: remove a rule Incus may hold — whatever its state, even not found; Incus answers. The current pair
    /// waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refuses a
    /// repeat and a rule already deleted; the handler answers those
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    internal string RequestDelete()
    {
        if (CreateIsWaiting)
        {
            throw new BusinessException("FIREWALL_RULE_NOT_READY",
                "Firewall rule is not yet provisioned. Wait for activation before deleting.");
        }

        string? externalRuleId = ExternalId;
        if (DeleteIsWaiting || IsTerminal || externalRuleId == null)
        {
            throw new BusinessException("FIREWALL_RULE_INVALID_STATE",
                $"A firewall rule that is {CurrentExistence} (asked: {DesiredExistence}) cannot be deleted through Incus.");
        }

        DesiredExistence = DesiredExistence.Deleted;
        PendingIntentId = Guid.NewGuid();
        return externalRuleId;
    }

    /// The delete command gave up (titan-ops architecture/desired-and-current.md#abandoned-request): DesiredExistence
    /// returns to Exists, so it counts against the quota again (it is still enforced on Incus), the reason is kept
    /// (titan-ops architecture/desired-and-current.md#request-failure) and the next delete is a new request.
    internal void AbandonRequest(string? reason, string? detail)
    {
        if (!DeleteIsWaiting)
        {
            throw new BusinessException("FIREWALL_RULE_INVALID_STATE", "No delete is waiting on this firewall rule.");
        }

        DesiredExistence = DesiredExistence.Exists;
        PendingIntentId = null;
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
    }

    /// Intent: remove a rule whose create failed before Incus had it. Nothing to send and nothing to wait for — the
    /// control plane is the only witness, which is why this request writes the current pair too
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    internal void DeleteLocally()
    {
        bool neverReachedIncus = ExternalId == null && !CreateIsWaiting && !IsTerminal;
        if (!neverReachedIncus)
        {
            throw new BusinessException("FIREWALL_RULE_INVALID_STATE",
                "Only a firewall rule whose creation failed can be deleted without Incus.");
        }

        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
    }
}
