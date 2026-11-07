using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Network;

public partial class Network : IAggregateRoot
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
    /// The operation waiting on Incus and the last one that failed (titan-ops features/network/design.md#network-operation).
    public NetworkOperation Operation { get; private set; } = NetworkOperation.None;
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
            || (CurrentExistence == CurrentExistence.NotCreated && Operation.PendingId != null));

    /// Counts toward MaxPublicIpv6 (titan-ops features/network/design.md#ipv6-quota): a network that counts toward
    /// MaxNetworks and holds a /64. NetworkRepository.CountPublicIpv6ActiveByTenantAsync writes the same test in SQL.
    public bool OccupiesPublicIpv6Quota => PublicIpv6Subnet != null && OccupiesQuota;

    /// Holds a range, whatever was asked of it (titan-ops features/network/design.md#network-lifecycle): Incus has the
    /// network, a delete in flight included, or is about to. The third rule with this current clause, beside the two
    /// quota rules, which also read desired: a network being deleted stops counting but keeps its range.
    /// NetworkRepository.HasCidrOverlapAsync writes the same test in SQL and asks HeldRange of the rows it names.
    public bool HoldsARange =>
        CurrentExistence == CurrentExistence.Exists
        || (CurrentExistence == CurrentExistence.NotCreated && Operation.PendingId != null);

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
            bool aboutToHold = CurrentExistence == CurrentExistence.NotCreated && Operation.PendingId != null;
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
            switch (Operation.Kind)
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

    private bool CreateIsWaiting => Operation.Kind == NetworkOperationKind.Create;
    private bool DeleteIsWaiting => Operation.Kind == NetworkOperationKind.Delete;
    /// A delete was asked and still waits, or the network is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsDeleted;

    private static string? AddressOrNull(string? reported)
    {
        if (reported == null || reported.Length == 0)
        {
            return null;
        }
        return reported;
    }

    // ---- The report of an operation's own command (titan-ops features/network/design.md#network-operation): the one thing that
    // ends an operation; CreateSucceeded and CreateFailed are in Network.Creation.cs, DeleteSucceeded and DeleteFailed in
    // Network.Deletion.cs. They write both axes, so none is an intent or a Mark*. Internal: NetworkRoutes ends them, so a
    // route the network lets go of goes back with it.

    /// The report names the operation of this kind the network waits on. A network already Deleted waits on nothing.
    private bool IsWaitingOn(Guid operationId, NetworkOperationKind kind) =>
        !IsDeleted && Operation.PendingId == operationId && Operation.Kind == kind;

    /// A Done must leave the network named on Incus, or nothing would be left to delete it through. Without a stored id
    /// the waiting operation can only be the one that makes it. Checked before the report writes anything; a worker that
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
        && Operation.PendingId == null;

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
}
