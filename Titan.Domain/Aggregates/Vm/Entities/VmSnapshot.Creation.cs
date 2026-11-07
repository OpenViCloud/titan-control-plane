using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Vm;

public partial class VmSnapshot
{
    /// Called by Vm.AddSnapshot after the VM checked itself, with the ids the handler chose: the snapshot's and its
    /// create's.
    internal static VmSnapshot Create(Guid id, Guid vmId, Guid tenantId, string name, Guid operationId)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "A snapshot needs an id.");
        }
        var snapshot = new VmSnapshot
        {
            Id = id,
            VmId = vmId,
            TenantId = tenantId,
            Name = name,
            Status = VmSnapshotStatus.Creating,
            CreatedAt = DateTime.UtcNow
        };
        snapshot.Operation = snapshot.Operation.Asked(operationId, DateTimeOffset.UtcNow, null);
        return snapshot;
    }

    /// The create command ended Done: records the id, and the snapshot is Available. A Done must leave the snapshot named
    /// on Incus, or nothing would be left to delete it through: without an id it is refused before anything is written (a
    /// worker that sends it has a bug, and does not retry a 422). A report that names another operation than the create
    /// the snapshot waits on is ignored and returns false.
    internal bool CreateSucceeded(Guid operationId, string? externalId)
    {
        if (!IsWaitingOn(operationId, VmSnapshotStatus.Creating))
        {
            return false;
        }
        bool doneWithoutAnId = ExternalId == null && string.IsNullOrEmpty(externalId);
        if (doneWithoutAnId)
        {
            throw new BusinessException("INVALID_STATUS", "ExternalId is required when a create ends Done.");
        }
        RecordTheId(externalId);
        Operation = Operation.Done();
        Status = VmSnapshotStatus.Available;
        return true;
    }

    /// The create command gave up: records the reason. Before Incus held a snapshot (the report brings no id) it is Failed;
    /// after (the report names it) it is Available. Ignored, returning false, when it names another operation than the
    /// create the snapshot waits on.
    internal bool CreateFailed(Guid operationId, string? externalId, string? reason, string? detail)
    {
        if (!IsWaitingOn(operationId, VmSnapshotStatus.Creating))
        {
            return false;
        }
        RecordTheId(externalId);
        Operation = Operation.Failed(reason, detail, DateTimeOffset.UtcNow);
        if (ExternalId is null)
        {
            Status = VmSnapshotStatus.Failed;
        }
        else
        {
            Status = VmSnapshotStatus.Available;
        }
        return true;
    }
}
