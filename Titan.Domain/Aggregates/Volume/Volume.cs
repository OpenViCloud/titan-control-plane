using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Volume;

public partial class Volume : IAggregateRoot
{
    /// A volume's name: 1 to 100 characters, not blank, any characters. The validators read it.
    public const int NameMaxLength = 100;

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = default!;
    public string ClusterId { get; private set; } = default!;
    public string VolumeOfferingId { get; private set; } = default!;
    public int SizeGb { get; private set; }
    /// The one status the API shows (titan-ops features/volume/design.md#volume-state-is-existence). Only an operation, a
    /// report ending it, or an observation at rest writes it, each through the moves of Volume.Creation.cs,
    /// Volume.Attachment.cs, Volume.Deletion.cs and Volume.Observation.cs.
    public VolumeStatus Status { get; private set; }
    /// The volume's own operation, the last one that failed, and whether Incus was last seen holding the volume while
    /// one waits.
    public VolumeOperation Operation { get; private set; } = VolumeOperation.None;
    /// The Titan VM Incus reports using this volume, or null. Written by observations and by a report that read the
    /// volume (titan-ops features/volume/design.md#attachment-from-used-by).
    public Guid? CurrentAttachedVmId { get; private set; }
    /// An instance that is not a Titan VM of this tenant uses it (attached outside Titan).
    public bool CurrentAttachedElsewhere { get; private set; }
    /// The VM the customer asked this volume to be attached to, or null (titan-ops
    /// architecture/desired-and-current.md#desired-and-current).
    public Guid? DesiredAttachedVmId { get; private set; }
    public string? ExternalId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// The volume's snapshots, each a copy on Incus named under the volume; rows are kept after Deleted
    /// (titan-ops features/volume/design.md#volume-snapshot). The repository loads them with the volume.
    private readonly List<VolumeSnapshot> _snapshots = [];
    public IReadOnlyCollection<VolumeSnapshot> Snapshots => _snapshots;

    /// What counts toward MaxVolumeSnapshotsPerVolume, and what refuses the volume's delete
    /// (titan-ops features/volume/design.md#counting-and-billing).
    public int SnapshotQuotaUsage => _snapshots.Count(s => s.CountsTowardQuota);

    private Volume() { }

    /// Whether Incus holds the volume as far as Titan last knew: Available, or null. While an operation waits it is what
    /// the operation last saw; at rest it is Available when the status is. Worked out, never stored, and not on /v1.
    public VolumeStatus? InstanceStatus
    {
        get
        {
            if (IsTransitionStatus)
            {
                return Operation.LastSeenStatus;
            }
            if (Status == VolumeStatus.Available)
            {
                return VolumeStatus.Available;
            }
            return null;
        }
    }

    /// An operation waits on its command: only the report carrying its id moves the status on.
    private bool IsTransitionStatus =>
        Status == VolumeStatus.Creating
        || Status == VolumeStatus.Attaching
        || Status == VolumeStatus.Detaching
        || Status == VolumeStatus.Deleting;

    private bool IncusHoldsIt => InstanceStatus == VolumeStatus.Available;

    /// Counts toward MaxVolumes (titan-ops features/volume/design.md#counting-and-billing): what Incus holds or is about to,
    /// unless its delete was pressed. A create that failed before Incus had it, or a volume Incus no longer has, does
    /// not count. VolumeRepository.CountActiveByTenantAsync writes the same test in SQL
    /// (titan-ops architecture/domain-methods.md#plain-code).
    public bool OccupiesQuota =>
        Status == VolumeStatus.Creating
        || Status == VolumeStatus.Available
        || ((Status == VolumeStatus.Attaching || Status == VolumeStatus.Detaching) && IncusHoldsIt);

    /// Billed while Incus holds it, a delete in flight included (titan-ops features/volume/design.md#counting-and-billing).
    /// VolumeRepository.ListBillableAsync writes the same test in SQL.
    public bool IsBilled => IncusHoldsIt;

    /// Deleted absorbs every later report and observation (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs).
    /// Until then the volume is still listed, still goes with its VM's delete and is still observed; the repository's
    /// queries write status <> 'Deleted' in SQL, the raw SQL that locks the attached volumes included
    /// (ListAttachedToVmLockedAsync; titan-ops architecture/domain-methods.md#plain-code).
    public bool IsDeleted => Status == VolumeStatus.Deleted;

    /// A delete was asked and still waits, or the volume is gone: a later delete is a repeat, answered like the first
    /// (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat).
    public bool IsDeletedOrDeleting => Status == VolumeStatus.Deleting || IsDeleted;

    /// Incus reports an instance using it, a Titan VM or not.
    private bool IsInUse => CurrentAttachedVmId != null || CurrentAttachedElsewhere;

    /// Incus holds it and nothing waits. Every intent but delete starts from here.
    private bool RestsOnIncus => Status == VolumeStatus.Available;

    /// The shape rule of a volume name: 1 to 100 characters, not blank, any characters.
    public static bool IsAName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= NameMaxLength;

    /// The shape rule of the name of one of this volume's snapshots: 1 to 100 characters, not blank.
    public static bool IsASnapshotName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= VolumeSnapshot.NameMaxLength;

    /// A volume is at least 1 GB.
    public static bool IsASize(int sizeGb) => sizeGb > 0;

    // ---- The report of the volume's own command (titan-ops features/volume/design.md#volume-reports): the one thing that
    // ends its operation; CreateSucceeded and CreateFailed are in Volume.Creation.cs, the attach and detach reports in
    // Volume.Attachment.cs, DeleteSucceeded and DeleteFailed in Volume.Deletion.cs.

    /// The report names the operation the volume waits on, which its status says.
    private bool IsWaitingOn(Guid operationId, VolumeStatus waiting) =>
        Status == waiting && Operation.PendingId == operationId;

    /// What the worker saw, whatever the outcome (a create that failed after Incus made the volume leaves a volume whose
    /// delete still reaches Incus): the provider id it brings, once, and, when it read the volume, that Incus holds it
    /// and which instances use it. Returns whether Incus was seen holding the volume by this report or before it.
    private VolumeStatus? NoteWhatTheWorkerSaw(string? externalId, bool sawTheVolume, Guid? attachedVmId,
        bool attachedElsewhere)
    {
        VolumeStatus? seen = InstanceStatus;
        bool bringsTheProviderId = ExternalId == null && !string.IsNullOrEmpty(externalId);
        if (bringsTheProviderId)
        {
            ExternalId = externalId;
            seen = VolumeStatus.Available;
        }
        if (sawTheVolume)
        {
            seen = VolumeStatus.Available;
            CurrentAttachedVmId = attachedVmId;
            CurrentAttachedElsewhere = attachedElsewhere;
        }
        return seen;
    }

    /// Throws rather than let a caller assert the id
    /// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
    private string GetExternalId()
    {
        if (ExternalId == null)
        {
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume has no provider resource yet.");
        }
        return ExternalId;
    }
}
