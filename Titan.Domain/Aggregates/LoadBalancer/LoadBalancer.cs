using System.Linq.Expressions;
using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.LoadBalancer;

/// A layer 4 load balancer on one network with public IPv6 (titan-ops features/load-balancer/design.md). Incus has no
/// status word for it: existence only, one request waiting at a time (titan-ops features/load-balancer/design.md#lb-lifecycle).
/// Listeners and backends carry both what was asked (IsDesired) and what Incus took (IsApplied), so an update that
/// gives up can go back (titan-ops architecture/desired-and-current.md#abandoned-request).
public class LoadBalancer : RequestingRoot<LoadBalancerRequestKind>
{
    /// A load balancer's name: 1 to 100 letters, digits, hyphens or underscores, starting with a letter or digit. The
    /// validators read these two, so the /v1 spec carries the same pattern.
    public const int NameMaxLength = 100;
    public const string NamePattern = "^[a-zA-Z0-9][a-zA-Z0-9_-]*$";

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
    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<LoadBalancerListener> _listeners = [];
    private readonly List<LoadBalancerBackend> _backends = [];
    public IReadOnlyList<LoadBalancerListener> Listeners => _listeners;
    public IReadOnlyList<LoadBalancerBackend> Backends => _backends;
    public IEnumerable<LoadBalancerListener> DesiredListeners => _listeners.Where(l => l.IsDesired);
    public IEnumerable<LoadBalancerBackend> DesiredBackends => _backends.Where(b => b.IsDesired);
    public IEnumerable<LoadBalancerListener> AppliedListeners => _listeners.Where(l => l.IsApplied);
    public IEnumerable<LoadBalancerBackend> AppliedBackends => _backends.Where(b => b.IsApplied);

    /// Counts toward MaxLoadBalancers (titan-ops features/load-balancer/design.md#lb-quota-and-billing): what Incus holds
    /// or is about to, unless its delete was pressed. A create that failed before Incus had it, or one Incus no longer
    /// has, does not count. Written once here; the repository counts with it in SQL.
    public static Expression<Func<LoadBalancer, bool>> OccupiesQuota { get; } = lb =>
        lb.DesiredExistence == DesiredExistence.Exists
        && (lb.CurrentExistence == CurrentExistence.Exists
            || (lb.CurrentExistence == CurrentExistence.NotCreated && lb.PendingRequestId != null));

    private LoadBalancer() { }

    /// The move the waiting request makes, for display.
    public LoadBalancerTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case LoadBalancerRequestKind.Create:
                    return LoadBalancerTransition.Creating;
                case LoadBalancerRequestKind.Update:
                    return LoadBalancerTransition.Updating;
                case LoadBalancerRequestKind.Delete:
                    return LoadBalancerTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private bool CreateIsWaiting => PendingRequestKind == LoadBalancerRequestKind.Create;
    private bool DeleteIsWaiting => PendingRequestKind == LoadBalancerRequestKind.Delete;
    /// A delete was asked and still waits, or the load balancer is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => DeleteIsWaiting || IsTerminal;

    /// Incus holds it and nothing waits: an update starts only from here.
    private bool RestsOnIncus =>
        PendingRequestId == null
        && ExternalId != null
        && CurrentExistence == CurrentExistence.Exists
        && DesiredExistence == DesiredExistence.Exists;

    /// Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    /// The network is checked by the caller through Network.ProviderIdForLoadBalancer: this aggregate holds no Network type
    /// (titan-ops architecture/code-structure.md#module-boundary).
    public static LoadBalancer Create(
        Guid tenantId, string name, Guid networkId, string externalNetworkId,
        string networkSubnet, string clusterId, LoadBalancerVip vip, IReadOnlyList<LoadBalancerListenerSpec> listeners,
        IReadOnlyList<Vm.Vm> backends)
    {
        RequireName(name);
        var loadBalancer = new LoadBalancer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NetworkId = networkId,
            ExternalNetworkId = externalNetworkId,
            NetworkSubnet = networkSubnet,
            ClusterId = clusterId,
            Name = name,
            VipIndex = vip.Index,
            Vip = vip.Address,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredExistence = DesiredExistence.Exists,
            CreatedAt = DateTimeOffset.UtcNow
        };
        loadBalancer.Ask(listeners, backends);
        loadBalancer.SetPending(NewRequest(LoadBalancerRequestKind.Create));
        return loadBalancer;
    }

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation).

    /// Whether the last request already asks for exactly this; the handler answers such a PUT itself
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool HasDesiredSpec(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Guid> backendVmIds)
    {
        return ListenersAre(listeners) && BackendsAre(backendVmIds);
    }

    private bool ListenersAre(IReadOnlyList<LoadBalancerListenerSpec> listeners)
    {
        var desiredListeners = DesiredListeners.ToList();
        return desiredListeners.Count == listeners.Count && listeners.All(spec => desiredListeners.Any(l => l.Is(spec)));
    }

    private bool BackendsAre(IReadOnlyList<Guid> backendVmIds)
    {
        var desiredVmIds = DesiredBackends.Select(b => b.VmId).ToHashSet();
        return desiredVmIds.Count == backendVmIds.Count && backendVmIds.All(desiredVmIds.Contains);
    }

    /// Returns the new request and the VIP: the update command is keyed on the request.
    public RequestTicket RequestUpdate(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Vm.Vm> backends)
    {
        if (!RestsOnIncus)
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE",
                "Load balancer must be on Incus, with nothing waiting, to change.");
        }
        if (ListenersAre(listeners) && BackendsAre(backends.Select(vm => vm.Id).ToList()))
        {
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "The load balancer already has this configuration.");
        }
        Ask(listeners, backends);
        PendingRequest<LoadBalancerRequestKind> request = NewRequest(LoadBalancerRequestKind.Update);
        SetPending(request);
        return RequestTicket.Of(request.Id, ExternalId!);
    }

    /// Titan-side only: Incus knows a load balancer by its VIP, never by its name
    /// (titan-ops features/load-balancer/design.md#lb-rename).
    public void Rename(string name)
    {
        bool beingOrBeenDeleted = DesiredExistence == DesiredExistence.Deleted || IsTerminal;
        if (beingOrBeenDeleted)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "Cannot rename a load balancer that is being or has been deleted.");
        RequireName(name);
        Name = name;
    }

    private static readonly System.Text.RegularExpressions.Regex NameShape = new(NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void RequireName(string? name)
    {
        if (name == null || name.Length == 0 || name.Length > NameMaxLength || !NameShape.IsMatch(name))
        {
            throw new BusinessException("INVALID_LOAD_BALANCER_NAME",
                $"A load balancer name is 1 to {NameMaxLength} letters, digits, hyphens or underscores, starting with a letter or digit.");
        }
    }

    /// A delete replaces a waiting update: the update's report then names a request no longer waiting, and is ignored.
    /// A load balancer with a provider id is deleted through Incus, even one Incus no longer has
    /// (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). Returns the request and the VIP.
    /// A load balancer that never reached Incus is deleted here, at once, and gets no ticket
    /// (titan-ops architecture/desired-and-current.md#delete-locally).
    public RequestTicket? RequestDelete()
    {
        if (DeleteIsWaiting)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "A delete is already in progress for this load balancer.");
        if (IsTerminal)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "The load balancer is already deleted.");
        // Never reached Incus: no command to send and no report coming.
        bool neverReachedIncus = ExternalId is null && PendingRequestId == null;
        if (neverReachedIncus)
        {
            DesiredExistence = DesiredExistence.Deleted;
            CurrentExistence = CurrentExistence.Deleted;
            SetPending(null);
            return null;
        }
        if (CreateIsWaiting || ExternalId is null)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "Load balancer is not yet on Incus.");
        PendingRequest<LoadBalancerRequestKind> request = NewRequest(LoadBalancerRequestKind.Delete);
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(request);
        return RequestTicket.Of(request.Id, ExternalId);
    }

    // ---- The report of a request's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that
    // ends a request. It writes both axes and the rows, so it is neither an intent nor a Mark*.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null: a stale
    /// give-up, a Done that arrived twice, the report of an update a delete replaced, a report for a load balancer
    /// already Deleted. A VIP the worker found is kept whatever the outcome. Done gives the request its effect and
    /// clears an older failure; Failed records the reason and puts desired back according to the kind
    /// (titan-ops architecture/resource-lifecycle.md#kinds).
    public PendingRequest<LoadBalancerRequestKind>? EndRequest(Guid requestId, LoadBalancerRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<LoadBalancerRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }
        RequireProviderIdOnDone(end.Outcome, ExternalId, end.ExternalId);

        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }

        if (end.Outcome == RequestOutcome.Done)
        {
            ClearFailure();
            if (waiting!.Kind == LoadBalancerRequestKind.Delete)
            {
                CurrentExistence = CurrentExistence.Deleted;
            }
            else
            {
                // Incus took what was asked.
                CurrentExistence = CurrentExistence.Exists;
                Apply();
            }
        }
        else
        {
            RecordFailure(end.Reason, end.Detail);
            if (waiting!.Kind == LoadBalancerRequestKind.Delete)
            {
                // A delete may have replaced a waiting update, whose rows are still the ones asked: what Incus took is
                // asked again, or desired and applied would differ with nothing waiting, and the replaced spec asked
                // again would look like a repeat.
                DesiredExistence = DesiredExistence.Exists;
                AskWhatIsApplied();
            }
            else if (waiting.Kind == LoadBalancerRequestKind.Update)
            {
                AskWhatIsApplied();
            }
            else if (bringsTheProviderId)
            {
                // The create's one write carries the whole spec: a VIP on Incus holds what was asked.
                Apply();
            }
        }

        SetPending(null);
        return waiting;
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): current only, never throws,
    // ends no request (titan-ops architecture/resource-lifecycle.md#invariants).

    /// The reconciler saw the VIP on its network.
    public void MarkObserved()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler did not find it. Never Deleted: only a delete's Done confirms that
    /// (titan-ops architecture/desired-and-current.md#existence-axis).
    public void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
    }

    // ---- desired and applied rows

    /// The configuration rules (titan-ops features/load-balancer/design.md#lb-resource-model) and each backend's own check;
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
            asked.Add((vm.Id, vm.ProviderIdForLoadBalancerBackend(NetworkId)));
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

    /// An update or a delete gave up: what Incus took is asked for again.
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
}
