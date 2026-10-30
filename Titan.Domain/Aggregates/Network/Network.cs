using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Domain.Aggregates.Network;

public class Network : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public string? ExternalId { get; private set; }
    public string? Cidr { get; private set; }
    public string? Gateway { get; private set; }
    // Only Mark* writes the current pair; only a request writes DesiredExistence
    // (titan-ops architecture/desired-and-current.md#desired-and-current). CurrentState is the Incus word, null while
    // Incus has no network (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    public NetworkStatus? CurrentState { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public Guid? PendingIntentId { get; private set; }
    // titan-ops architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public FirewallPolicy DefaultIngressPolicy { get; private set; } = FirewallPolicy.Allow;
    public FirewallPolicy DefaultEgressPolicy { get; private set; } = FirewallPolicy.Allow;
    // The network's public /64 and the router address OVH routes it to; null = no public IPv6
    // (titan-ops infra/incus/design.md#ipv6-address-plan). The index is unique per cluster among live networks.
    public int? PublicIpv6Index { get; private set; }
    public string? PublicIpv6Subnet { get; private set; }
    public string? PublicIpv6RouterAddress { get; private set; }
    /// When the delete of a network holding a /64 was confirmed: the allocator takes back the number given back
    /// longest ago (NetworkPublicIpv6.FirstFree).
    public DateTimeOffset? PublicIpv6ReleasedAt { get; private set; }

    private Network() { }

    /// Derived, never stored.
    public NetworkTransition? Transition
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
                return NetworkTransition.Deleting;
            }
            return NetworkTransition.Creating;
        }
    }

    private bool CreateIsWaiting => PendingIntentId != null && DesiredExistence == DesiredExistence.Exists;
    private bool DeleteIsWaiting => PendingIntentId != null && DesiredExistence == DesiredExistence.Deleted;

    /// Quota is checked inside the factory (titan-ops architecture/code-structure.md#tenant-typed-edge).
    public static Network Create(
        TenantAggregate tenant, int activeNetworkCount, int activePublicIpv6Count, string name, string clusterId,
        NetworkPublicIpv6? publicIpv6,
        FirewallPolicy defaultIngressPolicy = FirewallPolicy.Allow,
        FirewallPolicy defaultEgressPolicy = FirewallPolicy.Allow)
    {
        tenant.EnsureCanCreateNetwork(activeNetworkCount);
        if (publicIpv6 != null)
        {
            tenant.EnsureCanEnablePublicIpv6(activePublicIpv6Count);
        }
        var network = CreateUnchecked(tenant.Id, name, clusterId, defaultIngressPolicy, defaultEgressPolicy);
        if (publicIpv6 != null)
        {
            network.PublicIpv6Index = publicIpv6.Index;
            network.PublicIpv6Subnet = publicIpv6.Subnet;
            network.PublicIpv6RouterAddress = publicIpv6.RouterAddress;
        }
        return network;
    }

    /// Test seam — internal, reachable only from Titan.Tests. See Vm.CreateUnchecked.
    internal static Network CreateUnchecked(
        Guid tenantId, string name, string clusterId,
        FirewallPolicy defaultIngressPolicy = FirewallPolicy.Allow,
        FirewallPolicy defaultEgressPolicy = FirewallPolicy.Allow)
    {
        return new Network
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            ClusterId = clusterId,
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            PendingIntentId = Guid.NewGuid(),
            DefaultIngressPolicy = defaultIngressPolicy,
            DefaultEgressPolicy = defaultEgressPolicy,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs). Its name is already free
    /// for reuse, so a revived network could collide with a live one.
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): converge, never throw.

    /// The create command: Incus has the network, with the address it was given. The status is left to the
    /// reconciler, so a report lost after this still leaves a network whose delete reaches Incus.
    public void MarkCreated(string externalId, string cidr, string gateway)
    {
        if (IsTerminal)
        {
            return;
        }
        ExternalId = externalId;
        Cidr = cidr;
        Gateway = gateway;
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler read the network on Incus. Created fulfils a waiting create; Errored and Unavailable say it
    /// will not finish, so they end it. No observation withdraws a waiting delete. Returns true when this report
    /// fulfilled the waiting create, so the caller records "network.created" once.
    public bool MarkObserved(NetworkStatus status)
    {
        if (IsTerminal)
        {
            return false;
        }
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = status;
        if (!CreateIsWaiting)
        {
            return false;
        }
        if (status == NetworkStatus.Created)
        {
            PendingIntentId = null;
            return true;
        }
        bool createWillNotFinish = status == NetworkStatus.Errored || status == NetworkStatus.Unavailable;
        if (createWillNotFinish)
        {
            PendingIntentId = null;
        }
        return false;
    }

    /// The reconciler did not find the network. Never Deleted: only Titan's own delete command confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis). Ends a waiting create; a waiting delete keeps
    /// waiting for its command.
    public void MarkNotFound()
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

    /// The delete command confirmed Incus no longer has it. Idempotent, never refuses. Returns true when this
    /// report fulfilled the waiting delete, so the caller records "network.deleted" once.
    public bool MarkDeleted()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool fulfilled = DeleteIsWaiting;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        PendingIntentId = null;
        if (PublicIpv6Index != null)
        {
            PublicIpv6ReleasedAt = DateTimeOffset.UtcNow;
        }
        return fulfilled;
    }

    /// The create command gave up (titan-ops architecture/desired-and-current.md#request-failure): records why and ends
    /// the waiting create. A give-up with no create waiting is stale and records nothing; a delete's give-up is
    /// AbandonRequest.
    public void MarkRequestFailed(string? reason, string? detail)
    {
        if (IsTerminal || !CreateIsWaiting)
        {
            return;
        }
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
        PendingIntentId = null;
        // Incus never had the network, so nothing holds the /64 there; keeping it here would take one of the
        // cluster's 255 until the tenant deletes the row.
        bool incusNeverHadIt = ExternalId is null;
        if (incusNeverHadIt)
        {
            PublicIpv6Index = null;
            PublicIpv6Subnet = null;
            PublicIpv6RouterAddress = null;
        }
    }

    // ---- The provider id is handed out only after validating
    // (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).

    /// Intent. Follows Incus: whatever its word, even not found, a network with a provider id is deleted through
    /// Incus, which answers. The current pair waits for the delete command
    /// (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Refuses a repeat
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public string RequestDelete()
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

        DesiredExistence = DesiredExistence.Deleted;
        PendingIntentId = Guid.NewGuid();
        return ExternalId;
    }

    /// The delete command gave up (titan-ops architecture/desired-and-current.md#abandoned-request): the network is
    /// still on Incus, so DesiredExistence returns to Exists (it counts against the quota again), the reason is kept
    /// (titan-ops architecture/desired-and-current.md#request-failure) and the next delete is a new request. Not a Mark*:
    /// it changes what was asked for.
    public void AbandonRequest(string? reason, string? detail)
    {
        if (!DeleteIsWaiting)
        {
            throw new BusinessException("NETWORK_INVALID_STATE", "No delete is waiting on this network.");
        }

        DesiredExistence = DesiredExistence.Exists;
        PendingIntentId = null;
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
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

    public string BeginFirewallRule()
    {
        if (!IsUsable)
            throw new BusinessException("FIREWALL_NETWORK_NOT_READY", "Firewall rules can only be added to a network Incus has created.");
        return RequireExternalId();
    }

    /// A load balancer needs the network's public /64 for its VIP: Incus refuses one on a network with no uplink
    /// (titan-ops features/load-balancer/design.md#lb-needs-public-ipv6). Checked before the state.
    public string BeginLoadBalancer()
    {
        if (PublicIpv6Subnet == null)
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
        && PendingIntentId == null;

    /// Created implies MarkCreated set the id; this throws rather than let a caller assert it. Public for the
    /// reconciler's read model.
    public string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("NETWORK_INVALID_STATE", "Network is not yet provisioned.");

    // Titan-side metadata only, no provider call. A network being or already deleted has no name worth
    // changing.
    public void Rename(string name)
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
    public void DeleteLocally()
    {
        bool neverReachedIncus = ExternalId is null && !CreateIsWaiting && !IsTerminal;
        if (!neverReachedIncus)
        {
            throw new BusinessException("NETWORK_INVALID_STATE",
                "Only a network whose create failed before Incus had it can be deleted locally.");
        }
        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        PendingIntentId = null;
    }
}
