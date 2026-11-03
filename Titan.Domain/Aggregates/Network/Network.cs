using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public class Network : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public NetworkName Name { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public string? ExternalId { get; private set; }
    /// The range the customer asked for, held from the request on so a second create of the tenant meets it while
    /// this one still waits for Incus (titan-ops architecture/code-structure.md#logic-placement); null = Incus picks.
    public Ipv4Cidr? RequestedCidr { get; private set; }
    /// The address the worker reported for the create, verbatim: the range the create asked for as the customer typed
    /// it, or the one Incus picked when none was asked for
    /// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim); null until the Created report (or a
    /// give-up report that carried the id), and after one that carried no address.
    public string? Cidr { get; private set; }
    public string? Gateway { get; private set; }
    // Mark* and a request's report write the current pair; a request, and a report that it failed, write
    // DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current). CurrentState is the Incus word,
    // null while Incus has no network (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    public NetworkStatus? CurrentState { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public FirewallPolicy DefaultIngressPolicy { get; private set; } = FirewallPolicy.Allow;
    public FirewallPolicy DefaultEgressPolicy { get; private set; } = FirewallPolicy.Allow;
    // The /64 of the route the network was given (titan-ops infra/incus/design.md#ipv6-address-plan); null = no public
    // IPv6. Which route it is, and when it goes back, is the route's to know (ClusterIpv6Route).
    public string? PublicIpv6Subnet { get; private set; }

    public bool HasPublicIpv6 => PublicIpv6Subnet != null;

    // The one waiting request, as three columns (titan-ops architecture/resource-lifecycle.md#shape); all null or all
    // set, written by the factory and SetPending. Pending is the shape callers see.
    private Guid? PendingRequestId { get; set; }
    private NetworkRequestKind? PendingRequestKind { get; set; }
    private DateTimeOffset? PendingRequestAskedAt { get; set; }

    /// The request this network is waiting on, or null. Only the report carrying its id ends it
    /// (titan-ops architecture/resource-lifecycle.md#invariants).
    public PendingRequest<NetworkRequestKind>? Pending =>
        PendingRequestId == null
            ? null
            : PendingRequest<NetworkRequestKind>.Of(PendingRequestId.Value, PendingRequestKind!.Value, PendingRequestAskedAt!.Value);

    /// The network's rules, each a line in its ACL on Incus; rows are kept after Deleted
    /// (titan-ops features/network/design.md#firewall-rule-soft-delete). The repository loads them with the network.
    private readonly List<FirewallRule> _firewallRules = [];
    public IReadOnlyCollection<FirewallRule> FirewallRules => _firewallRules;

    /// What counts toward MaxFirewallRulesPerNetwork: the rules Incus holds or is about to hold
    /// (titan-ops features/network/design.md#firewall-rule-quota-counting).
    public int FirewallRuleQuotaUsage => _firewallRules.Count(r => r.CountsTowardQuota);

    private Network() { }

    /// The move the waiting request makes, for display.
    public NetworkTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case NetworkRequestKind.Create:
                    return NetworkTransition.Creating;
                case NetworkRequestKind.Delete:
                    return NetworkTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingRequestKind == NetworkRequestKind.Create;
    private bool DeleteIsWaiting => PendingRequestKind == NetworkRequestKind.Delete;

    private static PendingRequest<NetworkRequestKind> NewRequest(NetworkRequestKind kind) =>
        PendingRequest<NetworkRequestKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);

    private void SetPending(PendingRequest<NetworkRequestKind>? request)
    {
        PendingRequestId = request?.Id;
        PendingRequestKind = request?.Kind;
        PendingRequestAskedAt = request?.AskedAt;
    }

    /// The range the customer asked for is checked against the public IPv6 choice here, an invariant of two of the network's own
    /// attributes (titan-ops architecture/code-structure.md#logic-placement): the uplink of a network with public IPv6
    /// routes only the private ranges (titan-ops infra/incus/design.md#ipv6-address-plan). The validator answers the
    /// same case with 400 first, so NETWORK_CIDR_NOT_PRIVATE is not reached over HTTP.
    public static Network Create(
        Guid tenantId, NetworkName name, string clusterId,
        Ipv4Cidr? requestedCidr, bool wantsPublicIpv6,
        FirewallPolicy defaultIngressPolicy = FirewallPolicy.Allow,
        FirewallPolicy defaultEgressPolicy = FirewallPolicy.Allow)
    {
        bool cidrOutsideThePrivateRanges = wantsPublicIpv6 && requestedCidr != null && !requestedCidr.IsInsideAPrivateRange;
        if (cidrOutsideThePrivateRanges)
        {
            throw new BusinessException("NETWORK_CIDR_NOT_PRIVATE",
                "The CIDR of a network with public IPv6 must be inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16.");
        }
        var network = new Network
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            RequestedCidr = requestedCidr,
            ClusterId = clusterId,
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            DefaultIngressPolicy = defaultIngressPolicy,
            DefaultEgressPolicy = defaultEgressPolicy,
            CreatedAt = DateTimeOffset.UtcNow
        };
        network.SetPending(NewRequest(NetworkRequestKind.Create));
        return network;
    }

    /// The /64 of the route given to this network. The route keeps who holds it by the network's id, so the /64 comes
    /// after Create: once, and only while the create is waiting. Internal: NetworkRoutes gives the route with it.
    internal void ReceivePublicIpv6(string subnet)
    {
        if (HasPublicIpv6)
        {
            throw new InvalidOperationException($"Network {Id} already has a public IPv6 /64.");
        }
        if (!CreateIsWaiting)
        {
            throw new InvalidOperationException($"Network {Id} is not waiting for its create, so it takes no new /64.");
        }
        PublicIpv6Subnet = subnet;
    }

    /// Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs). Its name is already free
    /// for reuse, so a revived network could collide with a live one.
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what Incus has,
    // so no guard, and no request ends here (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the network on Incus. Created, Errored or Unavailable after a create is the network's state,
    /// not the create's ending: the create command's own report ended it.
    public void MarkObserved(NetworkStatus status)
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = status;
    }

    /// The reconciler did not find the network. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
    }

    private static string? AddressOrNull(string? reported)
    {
        if (reported == null || reported.Length == 0)
        {
            return null;
        }
        return reported;
    }

    // ---- The report of a request's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // a request. It writes both axes, so it is neither an intent nor a Mark*.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null: a stale
    /// give-up, a Done that arrived twice, a report for a network already Deleted. Done and Failed both record what the
    /// worker saw. Done gives the request its effect and clears an older failure; Failed records the reason and puts
    /// desired back according to the kind (titan-ops architecture/resource-lifecycle.md#kinds). Internal: NetworkRoutes
    /// ends it, so a route the network lets go of goes back with it.
    internal NetworkRequestEnded? EndRequest(Guid requestId, NetworkRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<NetworkRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }

        // What the worker saw, whatever the outcome: a create that failed after Incus made the network leaves a network
        // whose delete still reaches Incus. The addresses come with the name: the ones the create asked for or Incus
        // picked; an empty string is the same absence, and neither is stored.
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
            Cidr = AddressOrNull(end.Observed?.Cidr);
            Gateway = AddressOrNull(end.Observed?.Gateway);
        }
        if (end.Observed?.Status != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentState = end.Observed.Status;
        }

        var rulesWhoseDeleteEnded = new List<FirewallRule>();
        bool letGoOfPublicIpv6 = false;
        if (end.Outcome == RequestOutcome.Done)
        {
            RequestFailureReason = null;
            RequestFailureDetail = null;
            RequestFailedAt = null;
            if (waiting!.Kind == NetworkRequestKind.Delete)
            {
                // The delete removed the network's ACL, and every rule in it (titan-ops features/network/design.md#network-lifecycle):
                // every rule not yet Deleted goes with the network; the ones whose own delete was waiting are named for
                // their outcome.
                CurrentExistence = CurrentExistence.Deleted;
                CurrentState = null;
                foreach (FirewallRule rule in _firewallRules)
                {
                    bool itsOwnDeleteWasWaiting = rule.EndWithItsNetwork();
                    if (itsOwnDeleteWasWaiting)
                    {
                        rulesWhoseDeleteEnded.Add(rule);
                    }
                }
            }
        }
        else
        {
            RequestFailureReason = end.Reason;
            RequestFailureDetail = end.Detail;
            RequestFailedAt = DateTimeOffset.UtcNow;
            if (waiting!.Kind == NetworkRequestKind.Delete)
            {
                DesiredExistence = DesiredExistence.Exists;
            }
            else
            {
                // A failed create may be deleted, not retried: desired stays. Incus never had the network, so nothing
                // holds the /64 there; keeping it here would hold one of the cluster's 255 for as long as the network
                // lives.
                bool incusNeverHadIt = ExternalId is null;
                if (incusNeverHadIt && HasPublicIpv6)
                {
                    PublicIpv6Subnet = null;
                    letGoOfPublicIpv6 = true;
                }
            }
        }

        SetPending(null);
        return new NetworkRequestEnded(waiting, rulesWhoseDeleteEnded, letGoOfPublicIpv6);
    }

    // ---- The provider id is handed out only after validating
    // (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).

    /// Intent. Follows Incus: whatever its word, even not found, a network with a provider id is deleted through
    /// Incus, which answers. The current pair waits for the delete command
    /// (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refuses a repeat
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). Internal: NetworkDeletion is the only way in
    /// (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    internal RequestTicket RequestDelete()
    {
        if (DeleteIsWaiting)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "A delete is already in progress for this network.");
        }
        if (IsTerminal)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "The network is already deleted.");
        }
        if (CreateIsWaiting || ExternalId is null)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "Network is not yet provisioned.");
        }

        PendingRequest<NetworkRequestKind> request = NewRequest(NetworkRequestKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(request);
        return RequestTicket.Of(request.Id, ExternalId);
    }

    /// A VM is instantiated on its network's cluster, so it must ask for the network's own cluster
    /// (titan-ops features/catalog/design.md#cluster-is-a-real-choice). Checked before the state: another cluster is never right.
    public string BeginVmDeployment(string vmClusterId)
    {
        bool vmIsInAnotherCluster = vmClusterId != ClusterId;
        if (vmIsInAnotherCluster)
            throw new BusinessException("NETWORK_CLUSTER_MISMATCH",
                $"Network is in cluster '{ClusterId}', the VM was asked for cluster '{vmClusterId}'.");
        if (!IsUsable)
            throw new BusinessException("NETWORK_NOT_ACTIVE", "Network must be Created on Incus to deploy a VM.");
        return RequireExternalId();
    }

    // ---- Firewall rules: the network counts its rules and routes each call to the rule it names
    // (titan-ops architecture/code-structure.md#child-entity). Logic stays on FirewallRule; this is the way in.

    /// Intent: a new rule in this network's ACL. The network must be usable. The create request is the rule's own
    /// Pending.
    public FirewallRuleTicket AddFirewallRule(
        FirewallDirection direction, FirewallProtocol protocol,
        PortRange? ports, FirewallCidr cidr)
    {
        if (!IsUsable)
            throw new BusinessException("FIREWALL_NETWORK_NOT_READY", "Firewall rules can only be added to a network Incus has created.");
        int? startPort = null;
        int? endPort = null;
        if (ports != null)
        {
            startPort = ports.Start;
            endPort = ports.End;
        }
        FirewallRule rule = FirewallRule.Create(Id, TenantId, direction, protocol, startPort, endPort, cidr.Value);
        _firewallRules.Add(rule);
        return new FirewallRuleTicket(rule, RequireExternalId());
    }

    /// A rule this network does not have answers like a missing one, the same 404 the handler gives a rule of
    /// another network (titan-ops features/network/design.md#firewall-rule-soft-delete).
    private FirewallRule RequireFirewallRule(Guid ruleId)
    {
        foreach (FirewallRule rule in _firewallRules)
        {
            if (rule.Id == ruleId)
            {
                return rule;
            }
        }
        throw new NotFoundException("FIREWALL_RULE_NOT_FOUND", "Firewall rule not found.");
    }

    /// Intent on one rule: its delete goes through Incus; returns the request and the rule's provider id for the command.
    public RequestTicket RequestFirewallRuleDelete(Guid ruleId) => RequireFirewallRule(ruleId).RequestDelete();

    /// Intent on one rule whose create failed before Incus had it (titan-ops architecture/desired-and-current.md#delete-locally).
    public void DeleteFirewallRuleLocally(Guid ruleId) => RequireFirewallRule(ruleId).DeleteLocally();

    /// The report of the rule's own command (titan-ops architecture/resource-lifecycle.md#shape); null when it names
    /// another request than the one the rule waits on.
    public PendingRequest<FirewallRuleRequestKind>? EndFirewallRuleRequest(Guid ruleId, Guid requestId, FirewallRuleRequestEnd end) =>
        RequireFirewallRule(ruleId).EndRequest(requestId, end);

    // Observations of one rule (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkFirewallRuleObserved(Guid ruleId, FirewallRuleState state) =>
        RequireFirewallRule(ruleId).MarkObserved(state);

    public void MarkFirewallRuleNotFound(Guid ruleId) => RequireFirewallRule(ruleId).MarkNotFound();

    /// A load balancer needs the network's public /64 for its VIP: Incus refuses one on a network with no uplink
    /// (titan-ops features/load-balancer/design.md#lb-needs-public-ipv6). Checked before the state.
    public string BeginLoadBalancer()
    {
        if (!HasPublicIpv6)
            throw new BusinessException("LOAD_BALANCER_NEEDS_PUBLIC_IPV6", "A load balancer needs a network with public IPv6.");
        if (!IsUsable)
            throw new BusinessException("NETWORK_INVALID_STATE", "Load balancers can only be added to a network Incus has created.");
        return RequireExternalId();
    }

    /// Incus reports it Created and nothing is waiting: a network being deleted is not usable, even while Incus still
    /// shows it.
    private bool IsUsable =>
        CurrentState == NetworkStatus.Created
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists
        && PendingRequestId == null;

    /// Created implies the create's report set the id; this throws rather than let a caller assert it.
    private string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("NETWORK_INVALID_STATE", "Network is not yet provisioned.");

    // Titan-side metadata only, no provider call. A network being or already deleted has no name worth
    // changing.
    public void Rename(NetworkName name)
    {
        bool beingOrBeenDeleted = DesiredExistence == DesiredExistence.Deleted || IsTerminal;
        if (beingOrBeenDeleted)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "Cannot rename a network that is being or has been deleted.");
        }

        Name = name;
    }

    /// Intent: a network that never reached Incus has no command and no observation coming, so this writes the
    /// current pair itself (titan-ops architecture/desired-and-current.md#delete-locally).
    internal void DeleteLocally()
    {
        bool neverReachedIncus = ExternalId is null && !CreateIsWaiting && !IsTerminal;
        if (!neverReachedIncus)
        {
            throw new BusinessException("NETWORK_INVALID_STATE",
                "Only a network whose create failed before Incus had it can be deleted locally.");
        }
        // A create that gave up before Incus let go of the /64 (EndRequest). A network still holding one would keep
        // its route once Deleted here: no delete report follows a local delete, so nothing takes the route back.
        if (HasPublicIpv6)
        {
            throw new BusinessException("NETWORK_INVALID_STATE",
                "A network still holding its public IPv6 /64 cannot be deleted locally.");
        }
        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        SetPending(null);
    }
}
