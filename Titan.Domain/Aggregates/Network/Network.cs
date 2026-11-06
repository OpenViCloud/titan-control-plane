using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public class Network : ProviderResource<NetworkOperationKind>, IAggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public NetworkName Name { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public string? ExternalId { get; private set; }
    /// The range the customer asked for, held from the operation on so a second create of the tenant meets it while
    /// this one still waits for Incus (titan-ops architecture/code-structure.md#logic-placement); null = Incus picks.
    public Ipv4Cidr? ChosenCidr { get; private set; }
    /// The address the worker reported for the create, verbatim: the range the create asked for as the customer typed
    /// it, or the one Incus picked when none was asked for
    /// (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim); null until the Created report (or a
    /// give-up report that carried the id), and after one that carried no address.
    public string? Cidr { get; private set; }
    public string? Gateway { get; private set; }
    // Mark* and an operation's report write the current pair; an operation, and a report that it failed, write
    // DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current). CurrentState is the Incus word,
    // null while Incus has no network (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    public NetworkStatus? CurrentState { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public FirewallPolicy DefaultIngressPolicy { get; private set; } = FirewallPolicy.Allow;
    public FirewallPolicy DefaultEgressPolicy { get; private set; } = FirewallPolicy.Allow;
    // The /64 of the route the network was given (titan-ops infra/incus/design.md#ipv6-address-plan); null = no public
    // IPv6. Which route it is, and when it goes back, is the route's to know (ClusterIpv6Route).
    public string? PublicIpv6Subnet { get; private set; }

    public bool HasPublicIpv6 => PublicIpv6Subnet != null;

    /// The network's rules, each a line in its ACL on Incus; rows are kept after Deleted
    /// (titan-ops features/network/design.md#firewall-rule-soft-delete). The repository loads them with the network.
    private readonly List<FirewallRule> _firewallRules = [];
    public IReadOnlyCollection<FirewallRule> FirewallRules => _firewallRules;

    /// What counts toward MaxFirewallRulesPerNetwork: the rules Incus holds or is about to hold
    /// (titan-ops features/network/design.md#firewall-rule-quota-counting).
    public int FirewallRuleQuotaUsage => _firewallRules.Count(r => r.CountsTowardQuota);

    /// Counts toward MaxNetworks (titan-ops features/network/design.md#network-lifecycle): what Incus holds, whatever its
    /// word, or is about to hold. A delete in flight, a network Incus no longer has and a create that gave up before
    /// Incus had it do not count. NetworkRepository.CountActiveByTenantAsync writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool OccupiesQuota =>
        DesiredExistence == DesiredExistence.Exists
        && (CurrentExistence == CurrentExistence.Exists
            || (CurrentExistence == CurrentExistence.NotCreated && PendingOperationId != null));

    /// Counts toward MaxPublicIpv6 (titan-ops features/network/design.md#ipv6-quota): a network that counts toward
    /// MaxNetworks and holds a /64. NetworkRepository.CountPublicIpv6ActiveByTenantAsync writes the same test in SQL.
    public bool OccupiesPublicIpv6Quota => PublicIpv6Subnet != null && OccupiesQuota;

    /// Holds a range, whatever was asked of it (titan-ops features/network/design.md#network-lifecycle): Incus has the
    /// network, a delete in flight included, or is about to. The third rule with this current clause, beside the two
    /// quota rules, which also read desired: a network being deleted stops counting but keeps its range.
    /// NetworkRepository.HasCidrOverlapAsync writes the same test in SQL and asks HeldRange of the rows it names.
    public bool HoldsARange =>
        CurrentExistence == CurrentExistence.Exists
        || (CurrentExistence == CurrentExistence.NotCreated && PendingOperationId != null);

    /// The range this network holds, or null (titan-ops features/network/user-stories.md#us-net-intent-02): one Incus
    /// has keeps the address the worker reported, when that is an IPv4 CIDR (an address kept verbatim that is not one
    /// holds none); one Incus is about to have keeps the range the customer asked for; any other holds none.
    public Ipv4Cidr? HeldRange
    {
        get
        {
            if (CurrentExistence == CurrentExistence.Exists)
            {
                if (Ipv4Cidr.IsValid(Cidr))
                {
                    return Ipv4Cidr.Parse(Cidr);
                }
                return null;
            }
            bool aboutToHold = CurrentExistence == CurrentExistence.NotCreated && PendingOperationId != null;
            if (aboutToHold)
            {
                return ChosenCidr;
            }
            return null;
        }
    }

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs):
    /// its name is already free for reuse, so a revived network could collide with a live one. Until then the network
    /// is still listed, still holds its name and is still observed; the repository's list, name and reconciler queries
    /// write !IsDeleted in SQL (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => CurrentExistence == CurrentExistence.Deleted;

    private Network() { }

    /// The move the waiting operation makes, for display.
    public NetworkTransition? Transition
    {
        get
        {
            switch (PendingOperationKind)
            {
                case NetworkOperationKind.Create:
                    return NetworkTransition.Creating;
                case NetworkOperationKind.Delete:
                    return NetworkTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingOperationKind == NetworkOperationKind.Create;
    private bool DeleteIsWaiting => PendingOperationKind == NetworkOperationKind.Delete;
    /// A delete was asked and still waits, or the network is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsDeleted;

    /// The range the customer asked for is checked against the public IPv6 choice here, an invariant of two of the network's own
    /// attributes (titan-ops architecture/code-structure.md#logic-placement): the uplink of a network with public IPv6
    /// routes only the private ranges (titan-ops infra/incus/design.md#ipv6-address-plan). The validator answers the
    /// same case with 400 first, so NETWORK_CIDR_NOT_PRIVATE is not reached over HTTP.
    public static Network Create(
        Guid tenantId, NetworkName name, string clusterId,
        Ipv4Cidr? chosenCidr, bool wantsPublicIpv6,
        FirewallPolicy defaultIngressPolicy = FirewallPolicy.Allow,
        FirewallPolicy defaultEgressPolicy = FirewallPolicy.Allow)
    {
        bool cidrOutsideThePrivateRanges = wantsPublicIpv6 && chosenCidr != null && !chosenCidr.IsInsideAPrivateRange;
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
            ChosenCidr = chosenCidr,
            ClusterId = clusterId,
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            DefaultIngressPolicy = defaultIngressPolicy,
            DefaultEgressPolicy = defaultEgressPolicy,
            CreatedAt = DateTimeOffset.UtcNow
        };
        network.SetPending(NewOperation(NetworkOperationKind.Create));
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

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what Incus has,
    // so no guard, and no operation ends here (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler read the network on Incus. Created, Errored or Unavailable after a create is the network's state,
    /// not the create's ending: only the create command's own report ends it.
    public void MarkObserved(NetworkStatus status)
    {
        if (IsDeleted) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = status;
    }

    /// The reconciler did not find the network. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsDeleted) return;
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

    // ---- The report of an operation's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // an operation. It writes both axes, so it is neither an intent nor a Mark*.

    /// The command ended Done. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null: a Done that arrived twice, a report for a network already Deleted. Records what the worker saw,
    /// gives the operation its effect and clears an older failure (titan-ops architecture/resource-lifecycle.md#kinds).
    /// Internal: NetworkRoutes ends it, so a route the network lets go of goes back with it.
    /// A null status, cidr or gateway: the worker did not read it.
    internal NetworkOperationKind? FinishOperation(Guid operationId, string? externalId,
        NetworkStatus? status, string? cidr, string? gateway)
    {
        if (IsDeleted) return null;
        PendingOperation<NetworkOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        EnsureProviderIdOnDone(ExternalId, externalId);
        RecordWhatTheWorkerSaw(externalId, status, cidr, gateway);

        ClearFailure();
        if (waiting.Kind == NetworkOperationKind.Delete)
        {
            // The delete removed the network's ACL, and every rule in it (titan-ops features/network/design.md#network-lifecycle):
            // every rule not yet Deleted goes with the network.
            CurrentExistence = CurrentExistence.Deleted;
            CurrentState = null;
            foreach (FirewallRule rule in _firewallRules)
            {
                rule.EndWithItsNetwork();
            }
        }

        SetPending(null);
        return waiting.Kind;
    }

    /// The command gave up. Ends the waiting operation when the report names it; any other report is ignored and
    /// returns null: a stale give-up, a report for a network already Deleted. Records what the worker saw and the
    /// reason, and puts desired back according to the kind (titan-ops architecture/resource-lifecycle.md#kinds).
    /// Internal: NetworkRoutes ends it, so a /64 the network lets go of goes back with its route.
    /// A null status, cidr or gateway: the worker did not read it.
    internal NetworkOperationKind? FailOperation(Guid operationId, string? externalId,
        NetworkStatus? status, string? cidr, string? gateway, string? reason, string? detail)
    {
        if (IsDeleted) return null;
        PendingOperation<NetworkOperationKind>? waiting = WaitingOperationNamed(operationId);
        if (waiting == null)
        {
            return null;
        }
        RecordWhatTheWorkerSaw(externalId, status, cidr, gateway);

        RecordFailure(reason, detail);
        if (waiting.Kind == NetworkOperationKind.Delete)
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
            }
        }

        SetPending(null);
        return waiting.Kind;
    }

    private PendingOperation<NetworkOperationKind>? WaitingOperationNamed(Guid operationId)
    {
        PendingOperation<NetworkOperationKind>? waiting = Pending;
        bool reportIsForThePendingOperation = waiting != null && waiting.Id == operationId;
        if (!reportIsForThePendingOperation)
        {
            return null;
        }
        return waiting;
    }

    // What the worker saw, whatever the outcome: a create that failed after Incus made the network leaves a network whose
    // delete still reaches Incus. The addresses come with the name: the ones the create asked for or Incus picked; an
    // empty string is the same absence, and neither is stored.
    private void RecordWhatTheWorkerSaw(string? externalId, NetworkStatus? status, string? cidr, string? gateway)
    {
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
            CurrentExistence = CurrentExistence.Exists;
            Cidr = AddressOrNull(cidr);
            Gateway = AddressOrNull(gateway);
        }
        if (status != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentState = status;
        }
    }

    // ---- The provider id is handed out only after validating
    // (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).

    /// Intent. Follows Incus: whatever its word, even not found, a network with a provider id is deleted through
    /// Incus, which answers. With a provider id, the current pair waits for the delete command
    /// (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refuses a repeat
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). Internal: NetworkDeletion is the only way in
    /// (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    /// A network that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    internal OperationTicket? Delete()
    {
        if (DeleteIsWaiting)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "A delete is already in progress for this network.");
        }
        if (IsDeleted)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "The network is already deleted.");
        }
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && PendingOperationId == null;
        if (neverReachedIncus)
        {
            DeleteLocally();
            return null;
        }

        if (CreateIsWaiting || ExternalId is null)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "Network is not yet provisioned.");
        }

        PendingOperation<NetworkOperationKind> operation = NewOperation(NetworkOperationKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(operation);
        return OperationTicket.Of(operation.Id, ExternalId);
    }

    /// A VM is instantiated on its network's cluster, so it must ask for the network's own cluster
    /// (titan-ops features/catalog/design.md#cluster-is-a-real-choice). Checked before the state: another cluster is never right.
    public string ProviderIdForVm(string vmClusterId)
    {
        bool vmIsInAnotherCluster = vmClusterId != ClusterId;
        if (vmIsInAnotherCluster)
            throw new BusinessException("NETWORK_CLUSTER_MISMATCH",
                $"Network is in cluster '{ClusterId}', the VM was asked for cluster '{vmClusterId}'.");
        if (!IsUsable)
            throw new BusinessException("NETWORK_NOT_ACTIVE", "Network must be Created on Incus to deploy a VM.");
        return GetExternalId();
    }

    // ---- Firewall rules: the network counts its rules and routes each call to the rule it names
    // (titan-ops architecture/code-structure.md#child-entity). Logic stays on FirewallRule; this is the way in.

    /// Intent: a new rule in this network's ACL, with the id the handler chose. The network must be usable. The create
    /// operation is the rule's own Pending; its ticket carries the network's provider id, whose ACL the command writes to.
    public OperationTicket AddFirewallRule(
        Guid ruleId, FirewallDirection direction, FirewallProtocol protocol,
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
        FirewallRule rule = FirewallRule.Create(ruleId, Id, TenantId, direction, protocol, startPort, endPort, cidr.Value);
        _firewallRules.Add(rule);
        return rule.TicketForCreate(GetExternalId());
    }

    /// A rule this network does not have answers like a missing one, the same 404 the handler gives a rule of
    /// another network (titan-ops features/network/design.md#firewall-rule-soft-delete).
    private FirewallRule GetFirewallRule(Guid ruleId)
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

    /// Intent on one rule: its delete goes through Incus; returns the operation and the rule's provider id for the command,
    /// or no ticket when the rule never reached Incus and is deleted here.
    public OperationTicket? DeleteFirewallRule(Guid ruleId) => GetFirewallRule(ruleId).Delete();

    /// The report that the rule's own command ended Done (titan-ops architecture/resource-lifecycle.md#shape); null when
    /// it names another operation than the one the rule waits on.
    public FirewallRuleOperationKind? FinishFirewallRuleOperation(Guid ruleId, Guid operationId, string? externalId,
        FirewallRuleState? observedState) =>
        GetFirewallRule(ruleId).FinishOperation(operationId, externalId, observedState);

    /// The report that the rule's own command gave up; null when it names another operation than the one the rule
    /// waits on.
    public FirewallRuleOperationKind? FailFirewallRuleOperation(Guid ruleId, Guid operationId, string? externalId,
        FirewallRuleState? observedState, string? reason, string? detail) =>
        GetFirewallRule(ruleId).FailOperation(operationId, externalId, observedState, reason, detail);

    // Observations of one rule (titan-ops architecture/intent-and-observation.md#observation-converges): current only.

    public void MarkFirewallRuleObserved(Guid ruleId, FirewallRuleState state) =>
        GetFirewallRule(ruleId).MarkObserved(state);

    public void MarkFirewallRuleNotFound(Guid ruleId) => GetFirewallRule(ruleId).MarkNotFound();

    /// A load balancer needs the network's public /64 for its VIP: Incus refuses one on a network with no uplink
    /// (titan-ops features/load-balancer/design.md#lb-needs-public-ipv6). Checked before the state.
    public string ProviderIdForLoadBalancer()
    {
        if (!HasPublicIpv6)
            throw new BusinessException("LOAD_BALANCER_NEEDS_PUBLIC_IPV6", "A load balancer needs a network with public IPv6.");
        if (!IsUsable)
            throw new BusinessException("NETWORK_INVALID_STATE", "Load balancers can only be added to a network Incus has created.");
        return GetExternalId();
    }

    /// Incus reports it Created and nothing is waiting: a network being deleted is not usable, even while Incus still
    /// shows it.
    private bool IsUsable =>
        CurrentState == NetworkStatus.Created
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists
        && PendingOperationId == null;

    /// Created implies the create's report set the id; this throws rather than let a caller assert it.
    private string GetExternalId()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "Network is not yet provisioned.");
        }
        return ExternalId;
    }

    // Titan-side metadata only, no provider call. A network being or already deleted has no name worth
    // changing.
    public void Rename(NetworkName name)
    {
        bool beingOrBeenDeleted = DesiredExistence == DesiredExistence.Deleted || IsDeleted;
        if (beingOrBeenDeleted)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "Cannot rename a network that is being or has been deleted.");
        }

        Name = name;
    }

    /// A network that never reached Incus has no command and no observation coming, so Delete writes the
    /// current pair here.
    private void DeleteLocally()
    {
        // A create that gave up before Incus let go of the /64 (FailOperation). A network still holding one would keep
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
