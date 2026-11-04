using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Domain.Model;

/// An aggregate root on the provider that holds at most one waiting request of kind TKind, and the last request
/// that failed (titan-ops architecture/resource-lifecycle.md#shape). The state lives here once for every such type;
/// each type keeps its own requests, guards and EndRequest. RequestingRoot and RequestingChild carry the same
/// members, because a type has one base and the role is the base (RequestingBaseTests keeps the two the same).
public abstract class RequestingRoot<TKind> : AggregateRoot where TKind : struct, Enum
{
    // A request that failed (titan-ops architecture/desired-and-current.md#request-failure): a fixed reason and the
    // raw detail; the detail is admin-only, never on the /v1/tenant path (titan-ops
    // features/public-api/design.md#status-reason). Cleared when a later request ends Done.
    public string? RequestFailureReason { get; private set; }
    public string? RequestFailureDetail { get; private set; }
    public DateTimeOffset? RequestFailedAt { get; private set; }

    // The one waiting request, as three columns; all null or all set, written only through SetPending. EF maps them
    // by name. Pending is the shape callers see; the id is readable so a repository can write "a request is waiting"
    // in its own query (titan-ops architecture/domain-methods.md#plain-code).
    public Guid? PendingRequestId { get; private set; }
    protected TKind? PendingRequestKind { get; private set; }
    protected DateTimeOffset? PendingRequestAskedAt { get; private set; }

    /// The request this resource is waiting on, or null. Only the report carrying its id ends it (titan-ops
    /// architecture/resource-lifecycle.md#invariants).
    public PendingRequest<TKind>? Pending =>
        PendingRequestId == null
            ? null
            : PendingRequest<TKind>.Of(PendingRequestId.Value, PendingRequestKind!.Value, PendingRequestAskedAt!.Value);

    protected static PendingRequest<TKind> NewRequest(TKind kind) =>
        PendingRequest<TKind>.Of(Guid.NewGuid(), kind, DateTimeOffset.UtcNow);

    /// A Done must leave the resource named on the provider, or nothing would be left to delete it through. Without
    /// a stored id the waiting request can only be the one that makes it. Checked before the report writes anything;
    /// a worker that sends it has a bug, and does not retry a 422.
    protected void RequireProviderIdOnDone(RequestOutcome outcome, string? storedId, string? reportedId)
    {
        bool doneWithoutAnId = outcome == RequestOutcome.Done && storedId == null && string.IsNullOrEmpty(reportedId);
        if (doneWithoutAnId)
        {
            string request = Pending!.Kind.ToString().ToLowerInvariant();
            throw new BusinessException("INVALID_STATUS", $"ExternalId is required when a {request} ends Done.");
        }
    }

    protected void RecordFailure(string? reason, string? detail)
    {
        RequestFailureReason = reason;
        RequestFailureDetail = detail;
        RequestFailedAt = DateTimeOffset.UtcNow;
    }

    protected void ClearFailure()
    {
        RequestFailureReason = null;
        RequestFailureDetail = null;
        RequestFailedAt = null;
    }

    protected void SetPending(PendingRequest<TKind>? request)
    {
        PendingRequestId = request?.Id;
        PendingRequestKind = request?.Kind;
        PendingRequestAskedAt = request?.AskedAt;
    }
}
