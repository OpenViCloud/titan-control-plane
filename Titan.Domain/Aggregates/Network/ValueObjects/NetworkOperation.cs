using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Network;

/// What a network keeps about the operations on its resource on Incus (titan-ops features/network/design.md#network-operation):
/// the one it waits on (its id, which the provider command carries and the report brings back, its kind and when it was
/// asked) and the last one that failed (a fixed reason, the raw detail for admins, and when). Every move returns an
/// immutable object and leaves the old one as it was; the six columns keep the names they had before the object.
public sealed record NetworkOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public NetworkOperationKind? Kind { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }

    private NetworkOperation() { }

    public static NetworkOperation None { get; } = new();

    /// An operation is asked. The last failure stays until an operation ends Done. An empty id would match no report and
    /// could never end it.
    public NetworkOperation Asked(Guid id, NetworkOperationKind kind, DateTimeOffset at)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, Kind = kind, AskedAt = at };
    }

    /// The operation ended Done: nothing waits, and the last failure is cleared.
    public NetworkOperation Done() => None;

    /// The operation gave up: nothing waits, and the failure is recorded.
    public NetworkOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
        this with
        {
            PendingId = null,
            Kind = null,
            AskedAt = null,
            FailureReason = reason,
            FailureDetail = detail,
            FailedAt = at,
        };

    /// Nothing waits any more without an operation of its own having ended Done or given up (a delete that never reached
    /// the provider): the last failure stays.
    public NetworkOperation ReturnedToRest() => this with { PendingId = null, Kind = null, AskedAt = null };
}
