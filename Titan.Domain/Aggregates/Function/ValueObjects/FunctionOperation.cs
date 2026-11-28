using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Function;

/// What a function keeps about the operations on it (titan-ops features/ovc-function/design.md#function-lifecycle): the
/// one it waits on (its id, which the provider command carries and the report brings back, and when it was asked), the
/// last one that gave up (a fixed reason, the raw detail, and when), and, while an operation waits, what the function
/// cluster was last known to hold: Active (its Knative Service seen), NotFound (it had been there and was last seen
/// missing), or null (never seen while it waits). What the operation is, a deploy or a delete, is the function's status. Every
/// move returns an immutable object and leaves the old one as it was.
public sealed record FunctionOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }
    public FunctionStatus? LastSeenStatus { get; private init; }

    private FunctionOperation() { }

    public static FunctionOperation None { get; } = new();

    /// An operation is asked. The last failure stays until an operation ends Done. An empty id would match no report and
    /// could never end it.
    public FunctionOperation Asked(Guid id, DateTimeOffset at, FunctionStatus? seen)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, AskedAt = at, LastSeenStatus = seen };
    }

    public FunctionOperation Saw(FunctionStatus? status) => this with { LastSeenStatus = status };

    /// The operation ended Done: nothing waits, and the last failure is cleared.
    public FunctionOperation Done() => None;

    /// The operation gave up: nothing waits, and the failure is recorded.
    public FunctionOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
        this with
        {
            PendingId = null,
            AskedAt = null,
            LastSeenStatus = null,
            FailureReason = reason,
            FailureDetail = detail,
            FailedAt = at,
        };
}
