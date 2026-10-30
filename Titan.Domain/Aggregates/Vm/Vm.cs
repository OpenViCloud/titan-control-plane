using Titan.Domain.Exceptions;
using Titan.Domain.Model;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

namespace Titan.Domain.Aggregates.Vm;

public class Vm : AggregateRoot
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid NetworkId { get; private set; }
    public string Name { get; private set; } = default!;
    // Only an observation (Mark*) writes CurrentState and CurrentExistence; only a request writes DesiredState and
    // DesiredExistence (titan-ops architecture/desired-and-current.md#desired-and-current). CurrentState is the Incus
    // status word for word, null while Incus has no instance (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
    public VmState? CurrentState { get; private set; }
    public VmDesiredState DesiredState { get; private set; }
    public CurrentExistence CurrentExistence { get; private set; }
    public DesiredExistence DesiredExistence { get; private set; }
    public string? ExternalId { get; private set; }
    // Set by the first status with a running guest; the reconciler keeps watching a VM that once ran even after
    // its create gave up.
    public bool HasBeenRunning { get; private set; }
    // The create request has ended: fulfilled, dropped by Error or NotFound, given up, or replaced by a delete.
    // It ends once, run or not, so a later Start on a VM that never ran is a start, not a second create.
    public bool CreateFinished { get; private set; }
    public string? IpAddress { get; private set; }
    public string? PublicIpv6Address { get; private set; }
    // A provider command that gave up (titan-ops architecture/desired-and-current.md#request-failure). The reason is a
    // fixed text the tenant reads; the detail is the raw error, admin-only, never on the /v1/tenant path
    // (titan-ops features/public-api/design.md#status-reason).
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }
    public string VmOfferingId { get; private set; } = default!;
    public string TemplateId { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public int DiskSizeGb { get; private set; }
    public string? UserData { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    // Null means nothing is waiting (titan-ops features/vm/design.md#pending-intent).
    public Guid? PendingIntentId { get; private set; }

    /// The VM's snapshots, each an instance snapshot on Incus named under the instance; rows are kept after Deleted
    /// (titan-ops features/vm/design.md#vm-snapshot). The repository loads them with the VM.
    private readonly List<VmSnapshot> _snapshots = [];
    public IReadOnlyCollection<VmSnapshot> Snapshots => _snapshots;

    /// What counts toward MaxVmSnapshotsPerVm (titan-ops features/vm/design.md#vm-snapshot-counting).
    public int SnapshotQuotaUsage => _snapshots.Count(s => s.CountsTowardQuota);

    /// A snapshot command (create or revert) still touching the instance: the VM's delete waits for it.
    private bool ASnapshotCommandIsRunning => _snapshots.Any(s => s.BlocksVmDelete);

    /// Derived, not stored: a stored kind would be a second copy of the desired fields that can disagree.
    /// Create and Start both want Running; until the create has finished, Running is what it waits for.
    public VmIntent? PendingIntent
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
                return VmIntent.Delete;
            }
            if (DesiredState == VmDesiredState.Stopped)
            {
                return VmIntent.Stop;
            }
            if (!CreateFinished)
            {
                return VmIntent.Create;
            }
            return VmIntent.Start;
        }
    }

    /// The move a waiting request makes, for display. Null with the desired fields differing from the current
    /// ones is drift.
    public VmTransition? Transition
    {
        get
        {
            VmIntent? waiting = PendingIntent;
            switch (waiting)
            {
                case VmIntent.Create:
                    return VmTransition.Creating;
                case VmIntent.Start:
                    return VmTransition.Starting;
                case VmIntent.Stop:
                    return VmTransition.Stopping;
                case VmIntent.Delete:
                    return VmTransition.Deleting;
                default:
                    return null;
            }
        }
    }

    private Vm() { }

    /// Quota is checked inside the only way to obtain a Vm (titan-ops architecture/code-structure.md#tenant-typed-edge).
    public static Vm Create(
        TenantAggregate tenant,
        int activeVmCount,
        Guid networkId,
        string name,
        string vmOfferingId,
        string templateId,
        string clusterId,
        int diskSizeGb,
        string? userData = null)
    {
        tenant.EnsureCanCreateVm(activeVmCount);

        return CreateUnchecked(tenant.Id, networkId, name, vmOfferingId, templateId, clusterId,
            diskSizeGb, userData);
    }

    /// Test seam that skips the quota check; `internal`, so only Titan.Tests reaches it
    /// (titan-ops architecture/code-structure.md#tenant-typed-edge).
    internal static Vm CreateUnchecked(
        Guid tenantId,
        Guid networkId,
        string name,
        string vmOfferingId,
        string templateId,
        string clusterId,
        int diskSizeGb,
        string? userData = null)
    {
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
            CreatedAt = DateTimeOffset.UtcNow,
            // The create is a waiting request too: the first running status fulfils it, which tells the
            // "created" outcome apart from a start.
            PendingIntentId = Guid.NewGuid()
        };
    }

    /// Deleted absorbs every later Mark* (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// ComputeMeterService bills a VM whose instance exists and runs, so a late poll that revived a deleted VM
    /// would charge for a machine that no longer exists.
    public bool IsTerminal => CurrentExistence == CurrentExistence.Deleted;

    // ---- Intent (titan-ops architecture/intent-and-observation.md#intent-versus-observation): a request from a person, refused when illegal.
    // What a request may do in each Incus status is what Incus itself allows there
    // (titan-ops features/vm/design.md#requests-follow-incus). The provider carries out stop/start/delete, so the
    // provider id leaves only on the ticket (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    // A repeat or an already-done request is refused; the handler recognises it before calling
    // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).

    public VmIntentTicket RequestStop()
    {
        if (PendingIntent == VmIntent.Stop)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A stop is already in progress for this VM.");
        }
        if (PendingIntent != null)
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
        return Accept(VmDesiredState.Stopped, DesiredExistence.Exists);
    }

    /// Start on a Frozen instance resumes it: Incus answers `start` from Frozen by thawing the guest.
    public VmIntentTicket RequestStart()
    {
        if (PendingIntent == VmIntent.Start)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A start is already in progress for this VM.");
        }
        if (PendingIntent != null)
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
        return Accept(VmDesiredState.Running, DesiredExistence.Exists);
    }

    /// Any VM with a provider id can be deleted: the worker force-stops whatever status Incus reports and deletes
    /// the instance, and an instance already gone counts as deleted. Without an id there is nothing on the provider
    /// unless the create is still waiting (it may yet instantiate), so the ticket says FinishLocally and the caller
    /// calls DeleteLocally().
    public VmIntentTicket RequestDelete()
    {
        if (IsTerminal)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is already deleted.");
        }
        if (PendingIntent == VmIntent.Delete)
        {
            throw new BusinessException("VM_INVALID_STATUS", "A delete is already in progress for this VM.");
        }
        // A snapshot create or revert in flight would act on an instance being deleted
        // (titan-ops features/vm/design.md#vm-snapshot-counting): refused until it settles, on a VM with no provider id too,
        // so its row does not vanish under the snapshot command.
        if (ASnapshotCommandIsRunning)
        {
            throw new BusinessException("VM_SNAPSHOT_IN_PROGRESS", "A snapshot operation is in progress for this VM.");
        }

        bool reachedTheProvider = ExternalId != null;
        if (!reachedTheProvider)
        {
            EnsureNoCreateWaiting();
            return new VmIntentTicket(VmIntentOutcome.FinishLocally, Guid.NewGuid(), "");
        }

        return Accept(DesiredState, DesiredExistence.Deleted); // replaces a waiting stop/start (titan-ops features/vm/design.md#conflict-and-delete-wins)
    }

    /// Changes nothing: the caller re-sends the command under the same intent id
    /// (titan-ops features/vm/design.md#resend-waiting-intent). Refuses an intent the VM is not waiting on,
    /// which would act on a stale request.
    public VmIntentTicket ResendWaitingIntent(VmIntent intent)
    {
        bool thisIntentIsWaiting = PendingIntent == intent && PendingIntentId != null;
        if (!thisIntentIsWaiting)
        {
            throw new BusinessException("VM_INVALID_STATUS", $"No {intent} request is waiting for this VM.");
        }
        return new VmIntentTicket(VmIntentOutcome.Dispatch, PendingIntentId!.Value, RequireExternalId());
    }

    private VmIntentTicket Accept(VmDesiredState desiredState, DesiredExistence desiredExistence)
    {
        Guid intentId = Guid.NewGuid();
        string providerId = RequireExternalId();
        DesiredState = desiredState;
        DesiredExistence = desiredExistence;
        PendingIntentId = intentId;
        CreateFinished = true;
        return new VmIntentTicket(VmIntentOutcome.Dispatch, intentId, providerId);
    }

    /// The waiting kind is read BEFORE the report changes anything, as the report may end the create. A waiting
    /// delete is never fulfilled by a status: only MarkDeleted ends it.
    private VmIntent? Fulfil(VmIntent? waitingBeforeThisReport, VmDesiredState reached)
    {
        bool aStatusRequestWaits = waitingBeforeThisReport != null && waitingBeforeThisReport != VmIntent.Delete;
        bool reachedWhatWasAsked = aStatusRequestWaits && DesiredState == reached;
        if (!reachedWhatWasAsked)
        {
            return null;
        }
        PendingIntentId = null;
        CreateFinished = true;
        return waitingBeforeThisReport;
    }

    /// A create still waiting without a provider id may yet instantiate on the provider, so there is nothing safe
    /// to forget.
    private void EnsureNoCreateWaiting()
    {
        if (PendingIntent == VmIntent.Create)
        {
            throw new BusinessException("VM_INVALID_STATUS", "VM is still being created; it can be deleted once the provider has it.");
        }
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). Public for the VM reconciler's
    /// read model.
    public string RequireExternalId() =>
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
    /// and CurrentState still reads Running until the reconciler observes the outcome
    /// (titan-ops features/vm/design.md#conflict-and-delete-wins).
    private void EnsureNoStopOrDeleteWaiting()
    {
        bool stopOrDeleteWaiting = PendingIntent == VmIntent.Stop || PendingIntent == VmIntent.Delete;
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

    /// AddSnapshot checks the same rule; this hands out the id, and the caller needs both, so neither can be skipped.
    public string BeginSnapshot()
    {
        EnsureSnapshottable();
        return RequireExternalId();
    }

    /// The precondition of AddSnapshot, RevertSnapshot and BeginSnapshot, never called by a handler
    /// (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation).
    private void EnsureSnapshottable()
    {
        if (!IncusCanAttachOrSnapshot())
            throw new BusinessException("VM_INVALID_STATUS", "VM must be Running, Ready, Stopped or Frozen to snapshot or revert.");
        EnsureNoStopOrDeleteWaiting();
    }

    // ---- Snapshots: the VM keeps the per-VM cap and the "no delete under a snapshot command" rule, and routes each
    // call to the snapshot it names (titan-ops architecture/code-structure.md#child-entity). Logic stays on VmSnapshot;
    // this is the way in.

    /// Intent: a new snapshot of this VM. The VM must accept a snapshot (EnsureSnapshottable) and the tenant's cap is
    /// checked here on what this VM holds (titan-ops architecture/code-structure.md#tenant-typed-edge); the handler locks
    /// the VM row first so two requests cannot both count below the cap. The create request is the snapshot's own
    /// PendingIntentId.
    public VmSnapshot AddSnapshot(TenantAggregate tenant, string name)
    {
        EnsureSnapshottable();
        tenant.EnsureCanCreateVmSnapshot(SnapshotQuotaUsage);
        return AddSnapshotUnchecked(name);
    }

    /// Test seam — internal, reachable only from Titan.Tests: a snapshot of this VM with no check on the VM or the
    /// cap, so a test can put a VM into any shape. See CreateUnchecked.
    internal VmSnapshot AddSnapshotUnchecked(string name)
    {
        VmSnapshot snapshot = VmSnapshot.CreateUnchecked(Id, TenantId, name);
        _snapshots.Add(snapshot);
        return snapshot;
    }

    public VmSnapshot? SnapshotOrNull(Guid snapshotId)
    {
        foreach (VmSnapshot snapshot in _snapshots)
        {
            if (snapshot.Id == snapshotId)
            {
                return snapshot;
            }
        }
        return null;
    }

    /// A snapshot this VM does not have answers like a missing one, the same 404 a snapshot of another VM gets
    /// (titan-ops features/vm/design.md#vm-routes).
    public VmSnapshot RequireSnapshot(Guid snapshotId) =>
        SnapshotOrNull(snapshotId) ?? throw new NotFoundException("VM_SNAPSHOT_NOT_FOUND", "Snapshot not found.");

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
    public void DeleteLocally()
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
        PendingIntentId = null;
    }

    // ---- Observation (titan-ops architecture/intent-and-observation.md#observation-converges): a report of what the provider is,
    // so no transition guard. Running seen before a stop finishes is legitimate; throwing would fail
    // the internal callback and leave the VM stuck.

    /// The create command reports the provider id as soon as Incus holds the instance, before the reconciler sees
    /// its status, so a create that fails later still leaves a VM whose delete reaches the provider.
    public void MarkCreated(string externalId)
    {
        if (IsTerminal) return;
        ExternalId = externalId;
        CurrentExistence = CurrentExistence.Exists;
    }

    /// The reconciler saw the instance with this Incus status. Returns the intent it fulfilled, or null:
    /// Running or Ready fulfils a waiting create or start, Stopped a waiting stop. A guest-OS shutdown fulfils
    /// nothing (drift). Error ends a waiting create, stop or start — none can complete from it — and keeps a
    /// waiting delete, which only the delete command ends.
    public VmIntent? MarkObserved(VmState state, string? ipAddress, string? publicIpv6Address = null)
    {
        if (IsTerminal) return null;
        VmIntent? waitingBeforeThisReport = PendingIntent;
        CurrentExistence = CurrentExistence.Exists;
        CurrentState = state;
        IpAddress = ipAddress;
        PublicIpv6Address = publicIpv6Address;

        bool aGuestHasRun = state == VmState.Running || state == VmState.Ready || state == VmState.Frozen;
        if (aGuestHasRun)
        {
            HasBeenRunning = true;
        }

        if (state == VmState.Error)
        {
            DropAWaitingStatusRequest(waitingBeforeThisReport);
            return null;
        }
        bool runs = state == VmState.Running || state == VmState.Ready;
        if (runs)
        {
            return Fulfil(waitingBeforeThisReport, VmDesiredState.Running);
        }
        if (state == VmState.Stopped)
        {
            return Fulfil(waitingBeforeThisReport, VmDesiredState.Stopped);
        }
        return null;
    }

    /// The reconciler did not find the instance. Never Deleted: pointing at the wrong cluster also finds nothing
    /// (titan-ops architecture/desired-and-current.md#reconciler-never-dies); only the delete command reports Deleted.
    public void MarkNotFound()
    {
        if (IsTerminal) return;
        VmIntent? waitingBeforeThisReport = PendingIntent;
        CurrentExistence = CurrentExistence.NotFound;
        CurrentState = null;
        IpAddress = null;
        PublicIpv6Address = null;
        DropAWaitingStatusRequest(waitingBeforeThisReport);
    }

    /// A create, stop or start cannot complete on an instance in Error or gone, so it may be asked again; a
    /// waiting delete is legal from both and keeps waiting (titan-ops features/vm/design.md#pending-intent).
    private void DropAWaitingStatusRequest(VmIntent? waitingBeforeThisReport)
    {
        bool deleteIsWaiting = waitingBeforeThisReport == VmIntent.Delete;
        if (!deleteIsWaiting)
        {
            PendingIntentId = null;
        }
        CreateFinished = true;
    }

    /// The delete command saw the instance gone after Titan's own delete. Fulfilled is Delete if a delete was
    /// waiting; any other waiting intent can never happen now, so it is dropped without an outcome. Incus deletes an
    /// instance's snapshots with it (titan-ops features/vm/design.md#vm-snapshot), so every snapshot not yet Deleted goes
    /// with the VM, its waiting request ended; the ones whose own delete was waiting are named for their outcome.
    public VmDeletionOutcome MarkDeleted()
    {
        if (IsTerminal) return new VmDeletionOutcome(null, []);
        VmIntent? waitingBeforeThisReport = PendingIntent;
        CurrentExistence = CurrentExistence.Deleted;
        CurrentState = null;
        IpAddress = null;
        PublicIpv6Address = null;
        VmIntent? fulfilled = null;
        if (waitingBeforeThisReport == VmIntent.Delete)
        {
            fulfilled = VmIntent.Delete;
        }
        PendingIntentId = null;

        var snapshotsWhoseDeleteWasFulfilled = new List<VmSnapshot>();
        foreach (VmSnapshot snapshot in _snapshots)
        {
            bool itsOwnDeleteWasWaiting = snapshot.MarkDeleted();
            if (itsOwnDeleteWasWaiting)
            {
                snapshotsWhoseDeleteWasFulfilled.Add(snapshot);
            }
        }
        return new VmDeletionOutcome(fulfilled, snapshotsWhoseDeleteWasFulfilled);
    }

    /// The create command gave up (titan-ops architecture/desired-and-current.md#request-failure). Ends the waiting
    /// create — a failed create may be deleted, not retried — and leaves the observed fields to the reconciler. A
    /// waiting delete keeps waiting. A give-up for a create that already ended on a running guest is stale (an
    /// overlapping lease): the create succeeded, so it records no failure. A guest paused during its first boot has
    /// run while its create still waits; that create does end here.
    public void MarkRequestFailed(string? reason, string? detail)
    {
        if (IsTerminal) return;
        bool createWaits = PendingIntent == VmIntent.Create;
        bool staleGiveUp = !createWaits && HasBeenRunning;
        if (staleGiveUp)
        {
            return;
        }
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
        if (createWaits)
        {
            PendingIntentId = null;
        }
        CreateFinished = true;
    }

    /// The stop/start command gave up (titan-ops features/vm/design.md#intent-failed-callback). Not a Mark*: it
    /// changes what was asked for. Clears only the request it names, so a report for an older run cannot
    /// free a newer one; a waiting Delete waits for the provider
    /// (titan-ops architecture/desired-and-current.md#forget-on-provider-confirm). A repeated report returns null.
    public VmIntent? AbandonRequest(Guid intentId)
    {
        if (IsTerminal)
        {
            return null;
        }

        bool reportIsForTheWaitingRequest = PendingIntentId == intentId;
        bool waitingRequestIsStopOrStart = PendingIntent == VmIntent.Stop || PendingIntent == VmIntent.Start;
        if (!reportIsForTheWaitingRequest || !waitingRequestIsStopOrStart)
        {
            return null;
        }

        VmIntent failed = PendingIntent!.Value;
        if (CurrentState == VmState.Stopped)
        {
            DesiredState = VmDesiredState.Stopped;
        }
        else
        {
            DesiredState = VmDesiredState.Running;
        }
        PendingIntentId = null;
        return failed;
    }
}
