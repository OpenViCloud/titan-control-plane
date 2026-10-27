using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Compute.LoadBalancer;

/// A layer 4 load balancer on one network with public IPv6 (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md). Incus has no
/// status word for it: existence only, one request waiting at a time (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-lifecycle).
/// Listeners and backends carry both what was asked (IsDesired) and what Incus took (IsApplied), so an update that
/// gives up can go back (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#abandoned-request).
public class LoadBalancer
{
    public const int MaxListeners = 10;
    public const int MaxBackends = 20;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid NetworkId { get; private set; }
    /// Copies of the network's Incus name and public /64, fixed for the network's life: every command and the
    /// reconciler need them, and neither changes.
    public string ExternalNetworkId { get; private set; } = default!;
    public string NetworkSubnet { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public int VipIndex { get; private set; }
    public string Vip { get; private set; } = default!;
    public string? ExternalId { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public Guid? PendingIntentId { get; private set; }
    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<LoadBalancerListener> _listeners = [];
    private readonly List<LoadBalancerBackend> _backends = [];
    public IReadOnlyList<LoadBalancerListener> Listeners => _listeners;
    public IReadOnlyList<LoadBalancerBackend> Backends => _backends;
    public IEnumerable<LoadBalancerListener> DesiredListeners => _listeners.Where(l => l.IsDesired);
    public IEnumerable<LoadBalancerBackend> DesiredBackends => _backends.Where(b => b.IsDesired);
    public IEnumerable<LoadBalancerListener> AppliedListeners => _listeners.Where(l => l.IsApplied);
    public IEnumerable<LoadBalancerBackend> AppliedBackends => _backends.Where(b => b.IsApplied);

    private LoadBalancer() { }

    public LoadBalancerTransition? Transition
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
                return LoadBalancerTransition.Deleting;
            }
            if (CurrentExistence == CurrentExistence.NotCreated)
            {
                return LoadBalancerTransition.Creating;
            }
            return LoadBalancerTransition.Updating;
        }
    }

    private bool CreateIsWaiting => Transition == LoadBalancerTransition.Creating;
    private bool UpdateIsWaiting => Transition == LoadBalancerTransition.Updating;
    private bool DeleteIsWaiting => Transition == LoadBalancerTransition.Deleting;

    /// Incus holds it and nothing waits: an update starts only from here.
    private bool RestsOnIncus =>
        PendingIntentId == null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    /// Deleted absorbs (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    /// Quota is checked inside the factory (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#tenant-typed-edge). The network is
    /// checked by the caller through Network.BeginLoadBalancer: this aggregate holds no Networking type
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#module-boundary).
    public static LoadBalancer Create(
        Tenant tenant, int activeLoadBalancerCount, string name, Guid networkId, string externalNetworkId,
        string networkSubnet, string clusterId, LoadBalancerVip vip, IReadOnlyList<LoadBalancerListenerSpec> listeners,
        IReadOnlyList<Vm.Vm> backends)
    {
        tenant.EnsureCanCreateLoadBalancer(activeLoadBalancerCount);
        var loadBalancer = new LoadBalancer
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            NetworkId = networkId,
            ExternalNetworkId = externalNetworkId,
            NetworkSubnet = networkSubnet,
            ClusterId = clusterId,
            Name = name,
            VipIndex = vip.Index,
            Vip = vip.Address,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            PendingIntentId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        loadBalancer.Ask(listeners, backends);
        return loadBalancer;
    }

    // ---- Intent (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#intent-versus-observation).

    /// Whether the last request already asks for exactly this; the handler answers such a PUT itself
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool HasDesiredSpec(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Guid> backendVmIds)
    {
        var desiredListeners = DesiredListeners.ToList();
        var desiredVmIds = DesiredBackends.Select(b => b.VmId).ToHashSet();
        bool sameListeners = desiredListeners.Count == listeners.Count
                             && listeners.All(spec => desiredListeners.Any(l => l.Is(spec)));
        bool sameBackends = desiredVmIds.Count == backendVmIds.Count && backendVmIds.All(desiredVmIds.Contains);
        return sameListeners && sameBackends;
    }

    /// Returns the new request's id: the update command is keyed on it.
    public Guid RequestUpdate(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Vm.Vm> backends)
    {
        if (!RestsOnIncus)
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE",
                "Load balancer must be on Incus, with nothing waiting, to change.");
        }
        if (HasDesiredSpec(listeners, backends.Select(vm => vm.Id).ToList()))
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "The load balancer already has this configuration.");
        }
        Ask(listeners, backends);
        PendingIntentId = Guid.NewGuid();
        return PendingIntentId.Value;
    }

    /// Titan-side only: Incus knows a load balancer by its VIP, never by its name
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-rename).
    public void Rename(string name)
    {
        bool beingOrBeenDeleted = DesiredExistence == DesiredExistence.Deleted || IsTerminal;
        if (beingOrBeenDeleted)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "Cannot rename a load balancer that is being or has been deleted.");
        Name = name;
    }

    /// A delete replaces a waiting update (the update's report is then stale). A load balancer with a provider id is
    /// deleted through Incus, even one Incus no longer has
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#forget-on-provider-confirm).
    public string RequestDelete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "A delete is already in progress for this load balancer.");
        if (IsTerminal)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "The load balancer is already deleted.");
        if (CreateIsWaiting || ExternalId is null)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "Load balancer is not yet on Incus.");
        DesiredExistence = DesiredExistence.Deleted;
        PendingIntentId = Guid.NewGuid();
        return ExternalId;
    }

    /// An update or a delete command gave up (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#abandoned-request): the desired
    /// side moves back to what Incus took. Not a Mark*: it changes what was asked for.
    public void AbandonRequest(string? reason, string? detail)
    {
        if (DeleteIsWaiting)
        {
            DesiredExistence = DesiredExistence.Exists;
        }
        else if (UpdateIsWaiting)
        {
            AskWhatIsApplied();
        }
        else
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "No update or delete is waiting on this load balancer.");
        }
        PendingIntentId = null;
        RecordFailure(reason, detail);
    }

    /// Intent: a load balancer that never reached Incus has no command and no observation coming, so this writes the
    /// existence itself (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#delete-locally).
    public void DeleteLocally()
    {
        bool neverReachedIncus = ExternalId is null && !CreateIsWaiting && !IsTerminal;
        if (!neverReachedIncus)
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE",
                "Only a load balancer whose create failed before Incus had it can be deleted locally.");
        }
        DesiredExistence = DesiredExistence.Deleted;
        CurrentExistence = CurrentExistence.Deleted;
        PendingIntentId = null;
    }

    // ---- Observation (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#observation-converges): converge, never throw.

    /// The create command: Incus has the VIP and took the asked configuration. A repeat keeps the first id. Returns
    /// true when it fulfilled the waiting create.
    public bool MarkCreated(string externalId)
    {
        if (IsTerminal)
        {
            return false;
        }
        if (ExternalId == null)
        {
            ExternalId = externalId;
        }
        bool createWasWaiting = CreateIsWaiting;
        CurrentExistence = CurrentExistence.Exists;
        if (!createWasWaiting)
        {
            return false;
        }
        Apply();
        PendingIntentId = null;
        return true;
    }

    /// The update command of intentId: Incus took the asked configuration. A report of another intent is stale (a
    /// delete replaced it). Returns true when it fulfilled the waiting update.
    public bool MarkUpdated(Guid intentId)
    {
        bool fulfilsTheWaitingUpdate = !IsTerminal && UpdateIsWaiting && PendingIntentId == intentId;
        if (!fulfilsTheWaitingUpdate)
        {
            return false;
        }
        CurrentExistence = CurrentExistence.Exists;
        Apply();
        PendingIntentId = null;
        return true;
    }

    /// The reconciler saw the VIP on its network. No observation withdraws a waiting request.
    public void MarkObserved()
    {
        if (IsTerminal)
        {
            return;
        }
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler did not find it. Never Deleted: only Titan's own delete command confirms that
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#existence-axis). Ends a waiting create; a delete or update keeps
    /// waiting for its command.
    public void MarkNotFound()
    {
        if (IsTerminal)
        {
            return;
        }
        bool createWasWaiting = CreateIsWaiting;
        CurrentExistence = CurrentExistence.NotFound;
        if (createWasWaiting)
        {
            PendingIntentId = null;
        }
    }

    /// The delete command confirmed it gone. Idempotent. Returns true when it fulfilled the waiting delete.
    public bool MarkDeleted()
    {
        if (IsTerminal)
        {
            return false;
        }
        bool fulfilled = DeleteIsWaiting;
        CurrentExistence = CurrentExistence.Deleted;
        PendingIntentId = null;
        return fulfilled;
    }

    /// The create command gave up (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure). Stale with no create
    /// waiting; the other give-ups are AbandonRequest.
    public void MarkRequestFailed(string? reason, string? detail)
    {
        if (IsTerminal || !CreateIsWaiting)
        {
            return;
        }
        PendingIntentId = null;
        RecordFailure(reason, detail);
    }

    // ---- desired and applied rows

    /// The configuration rules (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-resource-model) and each backend's own check;
    /// the validator refuses the same first, so INVALID_LOAD_BALANCER_SPEC does not reach HTTP.
    private void Ask(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Vm.Vm> backends)
    {
        RequireSpec(listeners, backends);
        var asked = new List<(Guid VmId, string ExternalVmId)>();
        foreach (Vm.Vm vm in backends)
        {
            if (vm.TenantId != TenantId)
            {
                throw new BusinessException("LOAD_BALANCER_BACKEND_INVALID", $"VM '{vm.Id}' is not one of this tenant's.");
            }
            asked.Add((vm.Id, vm.BeginLoadBalancerBackend(NetworkId)));
        }

        foreach (LoadBalancerListener row in _listeners)
        {
            row.SetDesired(listeners.Any(row.Is));
        }
        foreach (LoadBalancerListenerSpec spec in listeners)
        {
            bool hasARow = _listeners.Any(row => row.Is(spec));
            if (!hasARow)
            {
                _listeners.Add(LoadBalancerListener.Desired(Id, spec));
            }
        }
        foreach (LoadBalancerBackend row in _backends)
        {
            row.SetDesired(asked.Any(a => a.VmId == row.VmId));
        }
        foreach ((Guid vmId, string externalVmId) in asked)
        {
            bool hasARow = _backends.Any(row => row.VmId == vmId);
            if (!hasARow)
            {
                _backends.Add(LoadBalancerBackend.Desired(Id, vmId, externalVmId));
            }
        }
        _listeners.RemoveAll(row => !row.IsDesired && !row.IsApplied);
        _backends.RemoveAll(row => !row.IsDesired && !row.IsApplied);
    }

    /// Incus took what was asked: the rows no longer asked for leave.
    private void Apply()
    {
        _listeners.RemoveAll(row => !row.IsDesired);
        _backends.RemoveAll(row => !row.IsDesired);
        foreach (LoadBalancerListener row in _listeners)
        {
            row.SetApplied();
        }
        foreach (LoadBalancerBackend row in _backends)
        {
            row.SetApplied();
        }
    }

    /// The update gave up: what Incus took is asked for again.
    private void AskWhatIsApplied()
    {
        _listeners.RemoveAll(row => !row.IsApplied);
        _backends.RemoveAll(row => !row.IsApplied);
        foreach (LoadBalancerListener row in _listeners)
        {
            row.SetDesired(true);
        }
        foreach (LoadBalancerBackend row in _backends)
        {
            row.SetDesired(true);
        }
    }

    private static void RequireSpec(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Vm.Vm> backends)
    {
        bool listenerCountFits = listeners.Count >= 1 && listeners.Count <= MaxListeners;
        bool backendCountFits = backends.Count >= 1 && backends.Count <= MaxBackends;
        bool portsFit = listeners.All(l => IsPort(l.Port) && IsPort(l.TargetPort));
        bool listenersDistinct = listeners.Select(l => (l.Protocol, l.Port)).Distinct().Count() == listeners.Count;
        bool backendsDistinct = backends.Select(vm => vm.Id).Distinct().Count() == backends.Count;
        bool valid = listenerCountFits && backendCountFits && portsFit && listenersDistinct && backendsDistinct;
        if (!valid)
        {
            throw new BusinessException("INVALID_LOAD_BALANCER_SPEC",
                $"A load balancer has 1 to {MaxListeners} listeners on distinct protocol and port, ports 1 to 65535, " +
                $"and 1 to {MaxBackends} distinct VMs.");
        }
    }

    private static bool IsPort(int port) => port >= 1 && port <= 65535;

    private void RecordFailure(string? reason, string? detail)
    {
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
    }
}
