using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Volume;

public partial class Volume
{
    /// Checks the volume, the VM (Vm.EnsureVolumeCanAttach) and the cluster; the provider ids come out only past all
    /// three (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id). The current attachment
    /// waits for Incus to be seen using the volume.
    public VolumeAttachProviderIds Attach(Vm.Vm vm)
    {
        bool available = RestsOnIncus && !IsInUse;
        if (!available)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be on Incus, unattached, with nothing waiting, to attach.");
        vm.EnsureVolumeCanAttach();
        string vmExternalId = vm.ExternalId!;
        if (ClusterId != vm.ClusterId)
            throw new BusinessException("VOLUME_CLUSTER_MISMATCH", "Volume and VM must be in the same cluster.");

        string externalVolumeId = GetExternalId();
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, InstanceStatus);
        Status = VolumeStatus.Attaching;
        DesiredAttachedVmId = vm.Id;
        return new VolumeAttachProviderIds(operationId, externalVolumeId, vmExternalId);
    }

    /// The detach command removes the disk from every instance using the volume, so a volume an instance outside
    /// Titan also uses is refused: Titan does not edit an instance it does not manage.
    public Values.OperationTicket Detach()
    {
        bool onATitanVmOnly = RestsOnIncus && CurrentAttachedVmId != null && !CurrentAttachedElsewhere;
        if (!onATitanVmOnly)
            throw new BusinessException("VOLUME_INVALID_STATE", "Volume must be attached to a Titan VM only, with nothing waiting, to detach.");
        string externalVolumeId = GetExternalId();
        Guid operationId = Guid.NewGuid();
        Operation = Operation.Asked(operationId, DateTimeOffset.UtcNow, InstanceStatus);
        Status = VolumeStatus.Detaching;
        DesiredAttachedVmId = null;
        return Values.OperationTicket.Of(operationId, externalVolumeId);
    }

    /// The attach command ended Done: the volume rests again, as Incus was seen holding it (the report reading the volume
    /// counts), with the attachment the worker read. Ignored, returning false, when it names another operation than the
    /// attach the volume waits on.
    public bool AttachSucceeded(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Attaching))
        {
            return false;
        }
        VolumeStatus? seen = NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Done();
        Status = seen ?? VolumeStatus.NotFound;
        return true;
    }

    /// The attach command gave up: the volume rests again as Incus was seen holding it, with the reason, and the customer's
    /// ask goes back to what Incus shows (titan-ops features/volume/design.md#volume-detach-fail-stays-attached). Ignored,
    /// returning false, when it names another operation than the attach the volume waits on.
    public bool AttachFailed(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Attaching))
        {
            return false;
        }
        VolumeStatus? seen = NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        DesiredAttachedVmId = CurrentAttachedVmId;
        Status = seen ?? VolumeStatus.NotFound;
        return true;
    }

    /// The detach command ended Done: the volume rests again, as Incus was seen holding it, with the attachment the
    /// worker read. Ignored, returning false, when it names another operation than the detach the volume waits on.
    public bool DetachSucceeded(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Detaching))
        {
            return false;
        }
        VolumeStatus? seen = NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Done();
        Status = seen ?? VolumeStatus.NotFound;
        return true;
    }

    /// The detach command gave up: the volume rests again with the reason and the customer's ask goes back to what Incus
    /// shows, so it stays attached (titan-ops features/volume/design.md#volume-detach-fail-stays-attached). Ignored,
    /// returning false, when it names another operation than the detach the volume waits on.
    public bool DetachFailed(Guid operationId, string? externalId,
        bool sawTheVolume, Guid? attachedVmId, bool attachedElsewhere, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VolumeStatus.Detaching))
        {
            return false;
        }
        VolumeStatus? seen = NoteWhatTheWorkerSaw(externalId, sawTheVolume, attachedVmId, attachedElsewhere);
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        DesiredAttachedVmId = CurrentAttachedVmId;
        Status = seen ?? VolumeStatus.NotFound;
        return true;
    }

    // ---- A VM's delete is not an operation of the volume (titan-ops architecture/resource-lifecycle.md#kinds): the volume's
    // own waiting operation, if any, is left to its own command's report.

    /// The VM this volume is asked to be attached to is being deleted, or its delete was done: Incus keeps the volume
    /// and detaches it when it deletes the VM (titan-ops features/volume/design.md#vm-delete-releases-volumes), so the
    /// attachment is no longer asked for. Only a volume with nothing waiting: an attach or detach of the customer's own
    /// keeps its desired attachment until its command reports.
    public void ReleaseFromVmDelete(Guid vmId)
    {
        bool askedForThatVmWithNothingWaiting = Operation.PendingId == null && DesiredAttachedVmId == vmId && !IsDeleted;
        if (askedForThatVmWithNothingWaiting)
        {
            DesiredAttachedVmId = null;
        }
    }

    /// The delete of the VM using this volume gave up: the VM stays, and so does the attachment its delete released.
    /// Only a volume with nothing waiting, released by that delete (nothing asked, Incus still shows that VM).
    public void KeepAttachmentAfterVmDeleteFailed(Guid vmId)
    {
        bool releasedByThatDelete = Operation.PendingId == null && DesiredAttachedVmId == null && CurrentAttachedVmId == vmId
                                    && !IsDeleted;
        if (releasedByThatDelete)
        {
            DesiredAttachedVmId = vmId;
        }
    }

    /// The delete command of the VM using this volume confirmed the instance gone: Incus keeps the volume and drops
    /// the attachment (titan-ops features/volume/design.md#vm-delete-releases-volumes).
    public void MarkDetachedByVmDelete(Guid vmId)
    {
        if (IsDeleted) return;
        if (CurrentAttachedVmId == vmId)
        {
            CurrentAttachedVmId = null;
        }
    }
}
