using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.LoadBalancer;

/// A layer 4 load balancer on one network with public IPv6 (titan-ops features/load-balancer/design.md). Incus has no
/// status word for it: it keeps one status and one operation waiting at a time (titan-ops features/load-balancer/design.md#lb-lifecycle).
/// Listeners and backends carry both what was asked (IsDesired) and what Incus took (IsApplied), so an update that
/// gives up can go back (titan-ops architecture/desired-and-current.md#abandoned-operation).
public partial class LoadBalancer : IAggregateRoot
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
    /// The one status the API shows (titan-ops features/load-balancer/design.md#lb-lifecycle). Only an operation, a report
    /// ending it, or an observation at rest writes it, each through the moves of LoadBalancer.Creation.cs,
    /// LoadBalancer.Update.cs, LoadBalancer.Deletion.cs and LoadBalancer.Observation.cs.
    public LoadBalancerStatus Status { get; private set; }
    /// The load balancer's own operation, the last one that failed, and whether Incus was last seen holding the load
    /// balancer while one waits.
    public LoadBalancerOperation Operation { get; private set; } = LoadBalancerOperation.None;
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

    /// Whether Incus holds the load balancer as far as Titan last knew: Available, or null. While an operation waits it is
    /// what the operation last saw; at rest it is Available when the status is. Worked out, never stored, and not on /v1.
    public LoadBalancerStatus? InstanceStatus
    {
        get
        {
            if (IsTransitionStatus)
            {
                return Operation.LastSeenStatus;
            }
            if (Status == LoadBalancerStatus.Available)
            {
                return LoadBalancerStatus.Available;
            }
            return null;
        }
    }

    /// An operation waits on its command: only the report carrying its id moves the status on.
    private bool IsTransitionStatus =>
        Status == LoadBalancerStatus.Creating
        || Status == LoadBalancerStatus.Updating
        || Status == LoadBalancerStatus.Deleting;

    /// Counts toward MaxLoadBalancers (titan-ops features/load-balancer/design.md#lb-quota-and-billing): what Incus holds
    /// or is about to, unless its delete was pressed. A create that failed before Incus had it, or one Incus no longer
    /// has, does not count. LoadBalancerRepository.CountActiveByTenantAsync writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool OccupiesQuota =>
        Status == LoadBalancerStatus.Creating
        || Status == LoadBalancerStatus.Available
        || (Status == LoadBalancerStatus.Updating && InstanceStatus == LoadBalancerStatus.Available);

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// Until then the load balancer is still listed, still holds its name and its VIP number, still sends to its
    /// backends and is still observed; the repository's queries write status <> 'Deleted' in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => Status == LoadBalancerStatus.Deleted;

    /// A delete was asked and still waits, or the load balancer is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => Status == LoadBalancerStatus.Deleting || IsDeleted;

    /// Incus holds it and nothing waits: an update starts only from here.
    private bool RestsOnIncus => Status == LoadBalancerStatus.Available;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation); Create, Update and Delete are
    // in LoadBalancer.Creation.cs, LoadBalancer.Update.cs and LoadBalancer.Deletion.cs.

    /// Whether the last operation already asks for exactly this; the handler answers such a PUT itself
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

    /// Titan-side only: Incus knows a load balancer by its VIP, never by its name
    /// (titan-ops features/load-balancer/design.md#lb-rename).
    public void Rename(string name)
    {
        if (IsDeletedOrDeleting)
            throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "Cannot rename a load balancer that is being or has been deleted.");
        Name = name;
    }

    private static readonly System.Text.RegularExpressions.Regex NameShape = new(NamePattern, System.Text.RegularExpressions.RegexOptions.Compiled);

    /// The shape rule of a load balancer name (titan-ops architecture/domain-methods.md#names-and-sizes).
    public static bool IsAName(string? name) =>
        name != null && name.Length > 0 && name.Length <= NameMaxLength && NameShape.IsMatch(name);

    // ---- The report of the load balancer's own command (titan-ops features/load-balancer/design.md#lb-lifecycle): the one thing
    // that ends its operation; the six reports are in LoadBalancer.Creation.cs, LoadBalancer.Update.cs and
    // LoadBalancer.Deletion.cs.

    /// The report names the operation the load balancer waits on, which its status says. A delete that replaced an update
    /// leaves the status Deleting, so the update's late report names an operation no longer waiting.
    private bool IsWaitingOn(Guid operationId, LoadBalancerStatus waiting) =>
        Status == waiting && Operation.PendingId == operationId;

    /// Whether Incus held the load balancer when a give-up arrived: as it was last seen, or Available when this report
    /// brings the VIP. Read before the operation ends.
    private LoadBalancerStatus? WhatIncusHeld(string? externalId)
    {
        LoadBalancerStatus? seen = InstanceStatus;
        if (RecordTheVip(externalId))
        {
            seen = LoadBalancerStatus.Available;
        }
        return seen;
    }

    /// The VIP the worker brings, once, whatever the outcome (a create that failed after Incus made the load balancer
    /// leaves one whose delete still reaches Incus). Returns whether this report brought the VIP the load balancer did
    /// not have yet.
    private bool RecordTheVip(string? externalId)
    {
        bool bringsTheVip = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheVip)
        {
            ExternalId = externalId;
        }
        return bringsTheVip;
    }

    // ---- desired and applied rows

    /// The configuration rules (titan-ops features/load-balancer/design.md#lb-resource-model) and each backend's own check;
    /// the validator refuses the same first, so INVALID_LOAD_BALANCER_SPEC does not reach HTTP.
    private void Ask(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Vm.Vm> backends)
    {
        if (!SpecFits(listeners, backends))
        {
            throw new BusinessException("INVALID_LOAD_BALANCER_SPEC",
                $"A load balancer has 1 to {MaxListeners} listeners on distinct protocol and port, ports 1 to 65535, " +
                $"and 1 to {MaxBackends} distinct VMs.");
        }
        var asked = new List<(Guid VmId, string ExternalVmId)>();
        foreach (Vm.Vm vm in backends)
        {
            if (vm.TenantId != TenantId)
            {
                throw new BusinessException("LOAD_BALANCER_BACKEND_INVALID", $"VM '{vm.Id}' is not one of this tenant's.");
            }
            vm.EnsureLoadBalancerCanSendTo(NetworkId);
            asked.Add((vm.Id, vm.ExternalId!));
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

    private static bool SpecFits(IReadOnlyList<LoadBalancerListenerSpec> listeners, IReadOnlyList<Vm.Vm> backends)
    {
        bool listenerCountFits = listeners.Count >= 1 && listeners.Count <= MaxListeners;
        bool backendCountFits = backends.Count >= 1 && backends.Count <= MaxBackends;
        bool portsFit = listeners.All(l => IsPort(l.Port) && IsPort(l.TargetPort));
        bool listenersDistinct = listeners.Select(l => (l.Protocol, l.Port)).Distinct().Count() == listeners.Count;
        bool backendsDistinct = backends.Select(vm => vm.Id).Distinct().Count() == backends.Count;
        return listenerCountFits && backendCountFits && portsFit && listenersDistinct && backendsDistinct;
    }

    private static bool IsPort(int port) => port >= 1 && port <= 65535;
}
