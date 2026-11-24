using System.Text.Json.Serialization;
using Titan.Domain.Aggregates.Volume;
using VolumeAggregate = Titan.Domain.Aggregates.Volume.Volume;

namespace Titan.Application.Queries.Compute.Volume.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse): never reused by a command, even
// where fields would match. One status (titan-ops features/volume/design.md#volume-state-is-existence), and the attachment: the
// current attachment is what Incus was last seen to hold, the desired one what was asked. The failure detail is the
// operator's, never the tenant's. RestoredFromBackup marks a volume an admin restored from a backup: it does not count
// toward the tenant's quota (titan-ops features/backup/design.md#restore-as-new-resource), so a client that counts
// usage from the list leaves it out.
public record VolumeQueryResult(
    Guid Id,
    string Name,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] VolumeStatus Status,
    Guid? CurrentAttachedVmId,
    bool CurrentAttachedElsewhere,
    Guid? DesiredAttachedVmId,
    string ClusterId,
    string VolumeOfferingId,
    int SizeGb,
    DateTimeOffset CreatedAt,
    string? RequestFailureReason,
    DateTimeOffset? RequestFailedAt,
    bool RestoredFromBackup)
{
    public static VolumeQueryResult From(VolumeAggregate v) =>
        new(v.Id, v.Name, v.Status, v.CurrentAttachedVmId, v.CurrentAttachedElsewhere, v.DesiredAttachedVmId,
            v.ClusterId, v.VolumeOfferingId, v.SizeGb, v.CreatedAt, v.Operation.FailureReason, v.Operation.FailedAt,
            v.RestoredFromBackup);
}
