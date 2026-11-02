using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

public class Vm : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid NetworkId { get; private set; }
    public string Name { get; private set; } = default!;
    // Only an observation (Mark*), a request's own report (EndRequest) or a delete with nothing on the provider
    // (DeleteLocally) writes CurrentState and CurrentExistence; only
    // a request, or a failed request's report putting it back, writes DesiredState and DesiredExistence
    // (titan-ops architecture/desired-and-current.md#desired-and-current).
    // CurrentState is the Incus status word for word, null while Incus has no instance
    // (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    public VmState? CurrentState { get; private set; }
    public VmDesiredState DesiredState { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public string? ExternalId { get; private set; }
    public string? IpAddress { get; private set; }
    public string? PublicIpv6Address { get; private set; }
    // A request that failed (titan-ops architecture/desired-and-current.md#request-failure). The reason is a fixed text the
    // tenant reads; the detail is the raw error, admin-only, never on the /v1/tenant path
    // (titan-ops features/public-api/design.md#status-reason). Cleared when a later request ends Done.
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public string VmOfferingId { get; private set; } = default!;
    public string TemplateId { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public int DiskSizeGb { get; private set; }
    public string? UserData { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // The one waiting request, as three columns (titan-ops architecture/resource-lifecycle.md#shape); all null or all
    // set, written by the factory and SetPending. Pending is the shape callers see.
    private Guid? PendingRequestId { get; set; }
    private VmRequestKind? PendingRequestKind { get; set; }
    private DateTimeOffset? PendingRequestAskedAt { get; set; }

    /// The request this VM is waiting on, or null. Only the report carrying its id ends it
    /// (titan-ops architecture/resource-lifecycle.md#invariants).
    public PendingRequest<VmRequestKind>? Pending =>
        PendingRequestId == null
            ? null
            : PendingRequest<VmRequestKind>.Of(PendingRequestId.Value, PendingRequestKind!.Value, PendingRequestAskedAt!.Value);

    /// The VM's snapshots, each an instance snapshot on Incus named under the instance; rows are kept after Deleted
    /// (titan-ops features/vm/design.md#vm-snapshot). The repository loads them with the VM.
    private readonly List<VmSnapshot> _snapshots = [];
    public IReadOnlyCollection<VmSnapshot> Snapshots => _snapshots;

    /// What counts toward MaxVmSnapshotsPerVm (titan-ops features/vm/design.md#vm-snapshot-counting).
    public int SnapshotQuotaUsage => _snapshots.Count(s => s.CountsTowardQuota);

    /// A snapshot command (create or revert) still touching the instance: the VM's delete waits for it.
    private bool ASnapshotCommandIsRunning => _snapshots.Any(s => s.BlocksVmDelete);

    /// The move the waiting request makes, for display. Null with the desired fields differing from the current
    /// ones is drift.
    public VmTransition? Transition
    {
        get
        {
            switch (PendingRequestKind)
            {
                case VmRequestKind.Create:
                    return VmTransition.Creating;
                case VmRequestKind.Start:
                    return VmTransition.Starting;
                case VmRequestKind.Stop:
                    return VmTransition.Stopping;
                case VmRequestKind.Delete:
                    return VmTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private Vm() { }

    public static Vm Create(
        Guid tenantId,
        Guid networkId,
        string name,
        string vmOfferingId,
        string templateId,
        string clusterId,
        int diskSizeGb,
        string? userData = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new Vm
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            NetworkId = networkId,
            Name = name,
            CurrentState = null,
            CurrentExistence = CurrentExistence.NotCreated,
            DesiredState = VmDesiredState.Running,
            DesiredExistence = DesiredExistence.Exists,
            VmOfferingId = vmOfferingId,
            TemplateId = templateId,
            ClusterId = clusterId,
            DiskSizeGb = diskSizeGb,
            UserData = userData,
            CreatedAt = now,
            // The create is a request like the others: the create command's Done ends it.
            PendingRequestId = Guid.NewGuid(),
            PendingRequestKind = VmRequestKind.Create,
            PendingRequestAskedAt = now
        };
    }

    /// Deleted absorbs every later report and observation (titan-ops architecture/resource-lifecycle.md#invariants).
    /// ComputeMeterService bills a VM whose instance exists and runs, so a late poll that revived a deleted VM
    /// would charge for a machine that no longer exists.
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation): a request from a person, refused when illegal.
    // What a request may do in each Incus status is what Incus itself allows there
    // (titan-ops features/vm/design.md#requests-follow-incus). The provider carries out stop/start/delete, so the
    // provider id leaves only on the ticket (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    // A repeat or an already-done request is refused; the handler recognises it before calling
    // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).

    public RequestTicket RequestStop()
    {
        if (PendingRequestKind == VmRequestKind.Stop)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop is already in progress for this VM.");
        }
        if (PendingRequestKind != null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another request for this VM is still in progress.");
        }
        if (CurrentState == VmState.Stopped)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already stopped.");
        }
        bool incusCanStopIt = CurrentState == VmState.Running
                           || CurrentState == VmState.Ready
                           || CurrentState == VmState.Frozen
                           || CurrentState == VmState.Error;
        if (!incusCanStopIt)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM can only be stopped while its instance is Running, Ready, Frozen or Error.");
        }
        return Accept(VmRequestKind.Stop, VmDesiredState.Stopped, DesiredExistence.Exists);
    }

    /// Start on a Frozen instance resumes it: Incus answers `start` from Frozen by thawing the guest.
    public RequestTicket RequestStart()
    {
        if (PendingRequestKind == VmRequestKind.Start)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A start is already in progress for this VM.");
        }
        if (PendingRequestKind != null)
        {
            throw new BusinessException("VM_INVALID_STATUS", "Another request for this VM is still in progress.");
        }
        bool alreadyRunning = CurrentState == VmState.Running || CurrentState == VmState.Ready;
        if (alreadyRunning)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already running.");
        }
        bool incusCanStartIt = CurrentState == VmState.Stopped || CurrentState == VmState.Frozen;
        if (!incusCanStartIt)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM can only be started while its instance is Stopped or Frozen.");
        }
        return Accept(VmRequestKind.Start, VmDesiredState.Running, DesiredExistence.Exists);
    }

    /// Any VM with a provider id can be deleted: the worker force-stops whatever status Incus reports and deletes
    /// the instance, and an instance already gone counts as deleted. Without an id there is nothing on the provider
    /// unless the create is still waiting (it may yet instantiate), so there is no ticket and VmDeletion calls
    /// DeleteLocally(). Internal: VmDeletion is the only way in (titan-ops architecture/domain-services.md#cross-aggregate-lock).
    internal RequestTicket? RequestDelete()
    {
        if (IsTerminal)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        }
        if (PendingRequestKind == VmRequestKind.Delete)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A delete is already in progress for this VM.");
        }
        // A snapshot create or revert in flight would act on an instance being deleted
        // (titan-ops features/vm/design.md#vm-snapshot-counting): refused until it settles.
        if (ASnapshotCommandIsRunning)
        {
            throw new BusinessException("VM_SNAPSHOT_IN_PROGRESS", "A snapshot operation is in progress for this VM.");
        }

        bool reachedTheProvider = ExternalId != null;
        if (!reachedTheProvider)
        {
            EnsureNoCreateWaiting();
            return null;
        }

        return Accept(VmRequestKind.Delete, DesiredState, DesiredExistence.Deleted); // replaces a waiting stop/start (titan-ops features/vm/design.md#conflict-and-delete-wins)
    }

    private RequestTicket Accept(VmRequestKind kind, VmDesiredState desiredState, DesiredExistence desiredExistence)
    {
        string providerId = RequireExternalId();
        PendingRequest<VmRequestKind> request = PendingRequest<VmRequestKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);
        DesiredState = desiredState;
        DesiredExistence = desiredExistence;
        SetPending(request);
        return RequestTicket.Of(request.Id, providerId);
    }

    private void SetPending(PendingRequest<VmRequestKind>? request)
    {
        PendingRequestId = request?.Id;
        PendingRequestKind = request?.Kind;
        PendingRequestAskedAt = request?.AskedAt;
    }

    /// A create still waiting without a provider id may yet instantiate on the provider, so there is nothing safe
    /// to forget.
    private void EnsureNoCreateWaiting()
    {
        if (PendingRequestKind == VmRequestKind.Create)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is still being created; it can be deleted once the provider has it.");
        }
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    private string RequireExternalId() =>
        ExternalId ?? throw new BusinessException("VM_INVALID_STATUS", "VM has no provider resource yet.");

    /// The VM half of Volume.Attach. It runs BEFORE the cluster check there, so a VM in the wrong state
    /// answers VM_INVALID_STATUS even when the clusters also differ. An allowlist: a new VmState is refused.
    public string BeginVolumeAttach()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to attach a volume.");
        EnsureNoStopOrDeleteWaiting();
        return RequireExternalId();
    }

    /// A load balancer of this VM's network may send to it: the instance is on Incus and its delete was not pressed.
    /// Any Incus state will do — the health check takes a stopped VM out of rotation
    /// (titan-ops features/load-balancer/design.md#lb-and-vm-network). Hands out the instance name the worker reads the
    /// MAC from (titan-ops features/load-balancer/design.md#backend-address).
    public string BeginLoadBalancerBackend(Guid networkId)
    {
        bool usable = NetworkId == networkId
                      && DesiredExistence == DesiredExistence.Exists
                      && CurrentExistence == CurrentExistence.Exists
                      && ExternalId != null;
        if (!usable)
            throw new BusinessException("LOAD_BALANCER_BACKEND_INVALID",
                $"VM '{Id}' must be on Incus, on the load balancer's network, and not being deleted.");
        return ExternalId!;
    }

    /// Incus attaches a volume to, and snapshots, an instance that is Running, Ready, Stopped or Frozen
    /// (titan-ops features/vm/design.md#requests-follow-incus); Error is not offered.
    private bool IncusCanAttachOrSnapshot()
    {
        bool instanceExists = CurrentExistence == CurrentExistence.Exists;
        bool statusAllowsIt = CurrentState == VmState.Running
                           || CurrentState == VmState.Ready
                           || CurrentState == VmState.Stopped
                           || CurrentState == VmState.Frozen;
        return instanceExists && statusAllowsIt;
    }

    /// Another aggregate's command against this VM would race a waiting stop or delete on the provider,
    /// and CurrentState still reads Running until the worker reports the outcome
    /// (titan-ops features/vm/design.md#conflict-and-delete-wins).
    private void EnsureNoStopOrDeleteWaiting()
    {
        bool stopOrDeleteWaiting = PendingRequestKind == VmRequestKind.Stop || PendingRequestKind == VmRequestKind.Delete;
        if (stopOrDeleteWaiting)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop or delete is in progress for this VM.");
        }
    }

    /// A shell session execs inside the guest, which needs its agent, which runs only while Incus reports Running
    /// or Ready (titan-ops features/vm/design.md#ssh-session). A waiting stop or delete would cut the session.
    public string ShellInstanceName()
    {
        bool instanceExists = CurrentExistence == CurrentExistence.Exists;
        bool guestRuns = CurrentState == VmState.Running || CurrentState == VmState.Ready;
        bool canHoldAShell = instanceExists && guestRuns;
        if (!canHoldAShell)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running or Ready to open an SSH session.");
        }
        EnsureNoStopOrDeleteWaiting();
        return RequireExternalId();
    }

    /// The precondition of AddSnapshot and RevertSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to snapshot or revert.");
        EnsureNoStopOrDeleteWaiting();
    }

    // ---- Snapshots: the VM counts its snapshots, keeps the "no delete under a snapshot command" rule, and routes each
    // call to the snapshot it names (titan-ops architecture/code-structure.md#child-entity). Logic stays on VmSnapshot;
    // this is the way in. The snapshot keeps today's request shape until its own rollout phase
    // (titan-ops architecture/resource-lifecycle.md#rollout).

    /// Intent: a new snapshot of this VM. The VM must accept a snapshot (EnsureSnapshottable). The create request is the
    /// snapshot's own PendingIntentId.
    public VmSnapshotTicket AddSnapshot(string name)
    {
        EnsureSnapshottable();
        VmSnapshot snapshot = VmSnapshot.Create(Id, TenantId, name);
        _snapshots.Add(snapshot);
        return new VmSnapshotTicket(snapshot, RequireExternalId());
    }

    /// A snapshot this VM does not have answers like a missing one, the same 404 a snapshot of another VM gets
    /// (titan-ops features/vm/design.md#vm-routes).
    private VmSnapshot RequireSnapshot(Guid snapshotId)
    {
        foreach (VmSnapshot snapshot in _snapshots)
        {
            if (snapshot.Id == snapshotId)
            {
                return snapshot;
            }
        }
        throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");
    }

    /// Intent on one snapshot: the VM must accept a revert (its own rule first), then the snapshot must rest on
    /// Incus; returns the snapshot's provider id for the command. Titan does not stop or start the VM for it.
    public string RevertSnapshot(Guid snapshotId)
    {
        VmSnapshot snapshot = RequireSnapshot(snapshotId);
        EnsureSnapshottable();
        return snapshot.Revert();
    }

    /// Intent on one snapshot: its delete goes through Incus; returns the snapshot's provider id for the command.
    public string RequestSnapshotDelete(Guid snapshotId) => RequireSnapshot(snapshotId).RequestDelete();

    /// Intent on one snapshot whose create failed before Incus had it (titan-ops architecture/desired-and-current.md#delete-locally).
    public void DeleteSnapshotLocally(Guid snapshotId) => RequireSnapshot(snapshotId).DeleteLocally();

    /// The snapshot's revert or delete command gave up (titan-ops architecture/desired-and-current.md#abandoned-request).
    public void AbandonSnapshotRequest(Guid snapshotId, string? reason, string? detail) =>
        RequireSnapshot(snapshotId).AbandonRequest(reason, detail);

    // Observations of one snapshot (titan-ops architecture/intent-and-observation.md#observation-converges), each
    // returning what the snapshot's own method returns.

    public bool MarkSnapshotCreated(Guid snapshotId, string externalId) =>
        RequireSnapshot(snapshotId).MarkCreated(externalId);

    public bool MarkSnapshotObserved(Guid snapshotId) => RequireSnapshot(snapshotId).MarkObserved();

    public bool MarkSnapshotReverted(Guid snapshotId) => RequireSnapshot(snapshotId).MarkReverted();

    public void MarkSnapshotNotFound(Guid snapshotId) => RequireSnapshot(snapshotId).MarkNotFound();

    public bool MarkSnapshotDeleted(Guid snapshotId) => RequireSnapshot(snapshotId).MarkDeleted();

    public void MarkSnapshotRequestFailed(Guid snapshotId, string? reason, string? detail) =>
        RequireSnapshot(snapshotId).MarkRequestFailed(reason, detail);

    /// Intent, Titan-side display name only. The aggregate carries it out itself, so the check sits inside
    /// the mutation (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    public void Rename(string newName)
    {
        if (IsTerminal)
            throw new BusinessException("VM_INVALID_STATUS", "VM cannot be renamed in its current state.");
        Name = newName;
    }

    /// Intent: a VM that never reached the provider has no command and no observation coming, so the
    /// aggregate writes the current fields itself (titan-ops architecture/desired-and-current.md#delete-locally).
    internal void DeleteLocally()
    {
        if (IsTerminal)
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        bool reachedTheProvider = ExternalId != null;
        if (reachedTheProvider)
            throw new BusinessException("VM_INVALID_STATUS", "VM exists at the provider; only its delete command can end it.");
        EnsureNoCreateWaiting();
        CurrentState = null;
        CurrentExistence = CurrentExistence.Deleted;
        DesiredExistence = DesiredExistence.Deleted;
        SetPending(null);
    }

    // ---- The report of a request's own command (titan-ops architecture/resource-lifecycle.md#shape): the one thing that ends
    // a request. It writes both axes — what the worker saw is current, and a failure puts desired back — so it is
    // neither an intent nor a Mark*.

    /// Ends the waiting request when the report names it; any other report is ignored and returns null: a stale
    /// give-up, a Done that arrived twice, a report for a VM already Deleted. Done and Failed both record what the
    /// worker saw. Done gives the request its effect and clears an older failure; Failed records the reason and puts
    /// desired back to current according to the kind (titan-ops architecture/resource-lifecycle.md#kinds).
    public VmRequestEnded? EndRequest(Guid requestId, VmRequestEnd end)
    {
        if (IsTerminal) return null;
        PendingRequest<VmRequestKind>? waiting = Pending;
        bool reportIsForTheWaitingRequest = waiting != null && waiting.Id == requestId;
        if (!reportIsForTheWaitingRequest)
        {
            return null;
        }

        // What the worker saw, whatever the outcome: a create that failed after Incus made the instance leaves a VM
        // whose delete still reaches the provider.
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(end.ExternalId);
        if (bringsTheProviderId)
        {
            ExternalId = end.ExternalId;
            CurrentExistence = CurrentExistence.Exists;
        }
        if (end.Observed != null)
        {
            CurrentExistence = CurrentExistence.Exists;
            CurrentState = end.Observed.Status;
            IpAddress = end.Observed.IpAddress;
            PublicIpv6Address = end.Observed.PublicIpv6Address;
        }

        var snapshotsWhoseDeleteEnded = new List<VmSnapshot>();
        if (end.Outcome == RequestOutcome.Done)
        {
            RequestFailureReason = null;
            RequestFailureDetail = null;
            RequestFailedAt = null;
            if (waiting!.Kind == VmRequestKind.Delete)
            {
                // Incus deletes an instance's snapshots with it (titan-ops features/vm/design.md#vm-snapshot): every
                // snapshot not yet Deleted goes with the VM; the ones whose own delete was waiting are named for
                // their outcome.
                CurrentExistence = CurrentExistence.Deleted;
                CurrentState = null;
                IpAddress = null;
                PublicIpv6Address = null;
                foreach (VmSnapshot snapshot in _snapshots)
                {
                    bool itsOwnDeleteWasWaiting = snapshot.MarkDeleted();
                    if (itsOwnDeleteWasWaiting)
                    {
                        snapshotsWhoseDeleteEnded.Add(snapshot);
                    }
                }
            }
        }
        else
        {
            RequestFailureReason = end.Reason;
            RequestFailureDetail = end.Detail;
            RequestFailedAt = DateTimeOffset.UtcNow;
            switch (waiting!.Kind)
            {
                case VmRequestKind.Delete:
                    DesiredExistence = DesiredExistence.Exists;
                    break;
                case VmRequestKind.Start:
                case VmRequestKind.Stop:
                    DesiredState = CurrentState == VmState.Stopped ? VmDesiredState.Stopped : VmDesiredState.Running;
                    break;
                case VmRequestKind.Create:
                    // A failed create may be deleted, not retried: desired stays, the reason says why it is not so.
                    break;
            }
        }

        SetPending(null);
        return new VmRequestEnded(waiting, snapshotsWhoseDeleteEnded);
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what the provider is,
    // so no transition guard and no request ends here (titan-ops architecture/resource-lifecycle.md#invariants). Running seen
    // before a stop finishes is legitimate; throwing would fail the internal callback and leave the VM stuck.

    /// The reconciler saw the instance with this Incus status and these addresses.
    public void MarkObserved(VmObservation observed)
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = observed.Status;
        IpAddress = observed.IpAddress;
        PublicIpv6Address = observed.PublicIpv6Address;
    }

    /// The reconciler did not find the instance. Never Deleted: pointing at the wrong cluster also finds nothing
    /// (titan-ops architecture/desired-and-current.md#reconciler-never-dies); only the delete command's Done ends a VM.
    public void MarkNotFound()
    {
        if (IsTerminal) return;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
        IpAddress = null;
        PublicIpv6Address = null;
    }
}
