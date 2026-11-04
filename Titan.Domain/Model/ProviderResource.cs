using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Model;

/// A resource Titan keeps on the provider: it waits on at most one operation of kind TKind, and remembers the last
/// operation that failed (titan-ops architecture/resource-lifecycle.md#shape). The state lives here once for every such
/// type, root or entity; each type keeps its own operations, guards and EndOperation. The role is IAggregateRoot or
/// IEntity (titan-ops architecture/domain-methods.md#provider-resource). An entity's pending operation is its own,
/// independent of its root's.
public abstract class ProviderResource<TKind> where TKind : struct, Enum
{
    // An operation that failed (titan-ops architecture/desired-and-current.md#operation-failure): a fixed reason and the
    // raw detail; the detail is admin-only, never on the /v1/tenant path (titan-ops
    // features/public-api/design.md#status-reason). Cleared when a later operation ends Done.
    public string? OperationFailureReason { get; private set; }
    public string? OperationFailureDetail { get; private set; }
    public DateTimeOffset? OperationFailedAt { get; private set; }

    // The one pending operation, as three columns; all null or all set, written only through SetPending. EF maps them
    // by name. Pending is the shape callers see; the id is readable so a repository can write "an operation is
    // pending" in its own query (titan-ops architecture/domain-methods.md#plain-code).
    public Guid? PendingOperationId { get; private set; }
    protected TKind? PendingOperationKind { get; private set; }
    protected DateTimeOffset? PendingOperationAskedAt { get; private set; }

    /// The operation this resource is waiting on, or null. Only the report carrying its id ends it (titan-ops
    /// architecture/resource-lifecycle.md#invariants).
    public PendingOperation<TKind>? Pending =>
        PendingOperationId == null
            ? null
            : PendingOperation<TKind>.Of(PendingOperationId.Value, PendingOperationKind!.Value, PendingOperationAskedAt!.Value);

    protected static PendingOperation<TKind> NewOperation(TKind kind) =>
        PendingOperation<TKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);

    /// A Done must leave the resource named on the provider, or nothing would be left to delete it through. Without
    /// a stored id the pending operation can only be the one that makes it. Checked before the report writes anything;
    /// a worker that sends it has a bug, and does not retry a 422.
    protected void RequireProviderIdOnDone(OperationOutcome outcome, string? storedId, string? reportedId)
    {
        bool doneWithoutAnId = outcome == OperationOutcome.Done && storedId == null && string.IsNullOrEmpty(reportedId);
        if (doneWithoutAnId)
        {
            string operation = Pending!.Kind.ToString().ToLowerInvariant();
            throw new BusinessException("INVALID_STATUS", $"ExternalId is required when a {operation} ends Done.");
        }
    }

    protected void RecordFailure(string? reason, string? detail)
    {
        OperationFailureReason = reason;
        OperationFailureDetail = detail;
        OperationFailedAt = DateTimeOffset.UtcNow;
    }

    protected void ClearFailure()
    {
        OperationFailureReason = null;
        OperationFailureDetail = null;
        OperationFailedAt = null;
    }

    protected void SetPending(PendingOperation<TKind>? operation)
    {
        PendingOperationId = operation?.Id;
        PendingOperationKind = operation?.Kind;
        PendingOperationAskedAt = operation?.AskedAt;
    }
}
