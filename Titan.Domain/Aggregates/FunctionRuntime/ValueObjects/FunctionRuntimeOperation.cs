using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.FunctionRuntime;

/// What a function runtime keeps about the sync in one cluster (titan-ops features/ovc-function/design.md#runtimes), as
/// a template does (VmTemplateOperation): the sync it waits on (its id, which the provider command carries and the
/// report brings back, and when it was asked), the last one that gave up (a fixed reason, the raw detail, and when),
/// and, while a sync waits, what the cluster was last known to be: Available (every Ready node seen holding the image),
/// NotFound (it had been there and was last seen missing), or null (never seen there). One operation, a sync, so the
/// status says what waits. Every move returns an immutable object and leaves the old one as it was.
public sealed record FunctionRuntimeOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }
    public FunctionRuntimeClusterStatus? LastSeenStatus { get; private init; }

    private FunctionRuntimeOperation() { }

    public static FunctionRuntimeOperation None { get; } = new();

    /// A sync is asked. The last failure stays until a sync ends Done. An empty id would match no report and could
    /// never end it.
    public FunctionRuntimeOperation Asked(Guid id, DateTimeOffset at, FunctionRuntimeClusterStatus? seen)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, AskedAt = at, LastSeenStatus = seen };
    }

    public FunctionRuntimeOperation Saw(FunctionRuntimeClusterStatus? status) => this with { LastSeenStatus = status };

    /// The sync ended Done: nothing waits, and the last failure is cleared.
    public FunctionRuntimeOperation Done() => None;

    /// The sync gave up: nothing waits, and the failure is recorded.
    public FunctionRuntimeOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
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
