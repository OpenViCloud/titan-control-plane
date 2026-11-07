using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Vm;

/// What a VM snapshot keeps about the operations on it (titan-ops features/vm/design.md#vm-snapshot): the one it waits on
/// (its id, which the provider command carries and the report brings back, and when it was asked), the last one that
/// failed (a fixed reason, the raw detail, and when), and, while an operation waits, whether Incus was last seen holding
/// the snapshot: Available, or null when it was not found or not seen yet. What the operation is, a create, a revert or
/// a delete, is the snapshot's status. Every move returns an immutable object and leaves the old one as it was.
public sealed record VmSnapshotOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }
    public VmSnapshotStatus? LastSeenStatus { get; private init; }

    private VmSnapshotOperation() { }

    public static VmSnapshotOperation None { get; } = new();

    /// An operation is asked. The last failure stays until an operation ends Done. An empty id would match no report and
    /// could never end it.
    public VmSnapshotOperation Asked(Guid id, DateTimeOffset at, VmSnapshotStatus? seen)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, AskedAt = at, LastSeenStatus = seen };
    }

    /// The reconciler saw the snapshot (Available) or did not find it (null).
    public VmSnapshotOperation Saw(VmSnapshotStatus? status) => this with { LastSeenStatus = status };

    /// The operation ended Done: nothing waits, nothing is held, and the last failure is cleared.
    public VmSnapshotOperation Done() => None;

    /// The operation gave up: nothing waits and nothing is held, and the failure is recorded.
    public VmSnapshotOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
        this with
        {
            PendingId = null,
            AskedAt = null,
            LastSeenStatus = null,
            FailureReason = reason,
            FailureDetail = detail,
            FailedAt = at,
        };

    /// Nothing waits any more without an operation of its own having ended Done or given up (a delete that never reached
    /// the provider, or an operation that went with its VM): the last failure stays.
    public VmSnapshotOperation ReturnedToRest() => this with { PendingId = null, AskedAt = null, LastSeenStatus = null };
}
