using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

/// One rule of a network: a line in the network's ACL on Incus (titan-ops features/network/design.md#firewall-on-incus).
/// A child of Network (titan-ops architecture/code-structure.md#child-entity): the network's delete taking
/// every rule with it is the network's invariant, so only the root reaches the methods below, and
/// handlers go through Network.AddFirewallRule, RequestFirewallRuleDelete, DeleteFirewallRuleLocally,
/// EndFirewallRuleRequest and MarkFirewallRule*.
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
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // The one waiting request, as three columns (titan-ops architecture/resource-lifecycle.md#shape); all null or all
    // set, written by the factory and SetPending. Its own, independent of the network's request.
    private Guid? PendingRequestId { get; set; }
    private FirewallRuleRequestKind? PendingRequestKind { get; set; }
    private DateTimeOffset? PendingRequestAskedAt { get; set; }

    /// The request this rule is waiting on, or null. Only the report carrying its id ends it
    /// (titan-ops architecture/resource-lifecycle.md#invariants).
    public PendingRequest<FirewallRuleRequestKind>? Pending =>
        PendingRequestId == null
            ? null
            : PendingRequest<FirewallRuleRequestKind>.Of(
                PendingRequestId.Value, PendingRequestKind!.Value, PendingRequestAskedAt!.Value);

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

    /// The move the waiting request makes, for display.
    public FirewallRuleTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case FirewallRuleRequestKind.Create:
                    return FirewallRuleTransition.Creating;
                case FirewallRuleRequestKind.Delete:
                    return FirewallRuleTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingRequestKind == FirewallRuleRequestKind.Create;
    private bool DeleteIsWaiting => PendingRequestKind == FirewallRuleRequestKind.Delete;

    private static PendingRequest<FirewallRuleRequestKind> NewRequest(FirewallRuleRequestKind kind) =>
        PendingRequest<FirewallRuleRequestKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);

    private void SetPending(PendingRequest<FirewallRuleRequestKind>? request)
    {
        PendingRequestId = request?.Id;
        PendingRequestKind = request?.Kind;
        PendingRequestAskedAt = request?.AskedAt;
    }

    /// Counted by occupancy toward MaxFirewallRulesPerNetwork (titan-ops features/network/design.md#firewall-rule-quota-counting):
    /// what Incus holds or is about to hold. A delete asked for stops counting at once; a failed create with nothing
    /// on Incus, and a rule Incus lost, do not count.
    public bool CountsTowardQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && CreateIsWaiting));

    /// Called by Network.AddFirewallRule after the network checked itself.
    internal static FirewallRule Create(
        Guid networkId, Guid tenantId,
        FirewallDirection direction, FirewallProtocol protocol,
        int? startPort, int? endPort, string cidr)
    {
        var rule = new FirewallRule
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
            CreatedAt = DateTimeOffset.UtcNow
        };
        rule.SetPending(NewRequest(FirewallRuleRequestKind.Create));
        return rule;
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, never throws,
    // ends no request (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the rule in its ACL.
    internal void MarkObserved(FirewallRuleState state)
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = state;
    }

    /// The reconciler did not find the rule in its ACL. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    internal void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
    }

    // ---- The report of the rule's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // its request.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null. Done of the
    /// create records the id, existence and the state read; Done of the delete ends the rule; Failed records the reason,
    /// keeps an id the worker found, and gives a delete back (titan-ops architecture/resource-lifecycle.md#kinds).
    internal PendingRequest<FirewallRuleRequestKind>? EndRequest(Guid requestId, FirewallRuleRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<FirewallRuleRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }

        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }
        if (end.ObservedState != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentState = end.ObservedState;
        }

        if (end.Outcome == RequestOutcome.Done)
        {
            RequestFailureReason = null;
            RequestFailureDetail = null;
            RequestFailedAt = null;
            if (waiting!.Kind == FirewallRuleRequestKind.Delete)
            {
                CurrentExistence = CurrentExistence.Deleted;
                CurrentState = null;
            }
        }
        else
        {
            RequestFailureReason = end.Reason;
            RequestFailureDetail = end.Detail;
            RequestFailedAt = DateTimeOffset.UtcNow;
            if (waiting!.Kind == FirewallRuleRequestKind.Delete)
            {
                // Still in the ACL and still enforced: it counts against the quota again.
                DesiredExistence = DesiredExistence.Exists;
            }
        }

        SetPending(null);
        return waiting;
    }

    /// Incus deleted the network's ACL, and this rule with it: the network's delete Done ends the rule, whatever it waited
    /// on. Returns true when the rule's own delete was waiting, so the caller records its outcome once.
    internal bool EndWithItsNetwork()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool itsOwnDeleteWasWaiting = DeleteIsWaiting;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        SetPending(null);
        return itsOwnDeleteWasWaiting;
    }

    /// Intent: remove a rule Incus may hold — whatever its state, even not found; Incus answers. The current pair
    /// waits for the delete command (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refuses a
    /// repeat and a rule already deleted; the handler answers those
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    internal RequestTicket RequestDelete()
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

        PendingRequest<FirewallRuleRequestKind> request = NewRequest(FirewallRuleRequestKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(request);
        return RequestTicket.Of(request.Id, externalRuleId);
    }

    /// The reconciler lists only rules with an ExternalId (FirewallRuleRepository.ListReconcilableAsync), so this
    /// cannot fire on that path; it throws rather than let a caller assert the id.
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("FIREWALL_RULE_INVALID_STATE", "Firewall rule has no provider resource yet.");

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
        SetPending(null);
    }
}
