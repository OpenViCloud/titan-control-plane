using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Vm;

/// What a VM keeps about the operations on its instance (titan-ops features/vm/design.md#vm-status): the one it waits
/// on (its id, which the provider command carries and the report brings back, and when it was asked), the last one that
/// failed (a fixed reason, the raw detail for admins, and when), and the Incus status seen while an operation waits.
/// The status seen is null for a VM at rest; when its Status is one of the five Incus statuses, that is the Incus
/// status then. A snapshot create or revert waits on the snapshot's command, so it holds no id and no time, only the
/// status seen. Every move returns an immutable object and leaves the old one as it was.
public sealed record VmOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }
    public VmStatus? LastSeenStatus { get; private init; }

    private VmOperation() { }

    public static VmOperation None { get; } = new();

    /// An operation is asked, or (with no id) a snapshot starts. The last failure stays until an operation ends Done.
    /// An empty id would match no report and could never end it.
    public VmOperation Asked(Guid? id, DateTimeOffset? at, VmStatus? seen)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, AskedAt = at, LastSeenStatus = seen };
    }

    /// The reconciler or a report saw this Incus status (null: none, the instance was not found).
    public VmOperation Saw(VmStatus? status) => this with { LastSeenStatus = status };

    /// The operation ended Done: nothing waits, nothing is held, and the last failure is cleared.
    public VmOperation Done() => None;

    /// The operation gave up: nothing waits and no status is held, and the failure is recorded.
    public VmOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
        this with
        {
            PendingId = null,
            AskedAt = null,
            LastSeenStatus = null,
            FailureReason = reason,
            FailureDetail = detail,
            FailedAt = at,
        };

    /// The VM is at rest again without an operation of its own having ended Done or given up (a snapshot ended, or a
    /// delete that never reached the provider): the last failure stays.
    public VmOperation ReturnedToRest() => this with { PendingId = null, AskedAt = null, LastSeenStatus = null };
}
