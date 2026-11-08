using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.VmTemplate;

/// What a template keeps about the sync in one cluster (titan-ops features/catalog/design.md#template-state-is-existence): the
/// sync it waits on (its id, which the provider command carries and the report brings back, and when it was asked), the
/// last one that gave up (a fixed reason, the raw detail, and when), and, while a sync waits, whether the cluster was last
/// seen holding the alias: Available, or null when it was not found or not seen yet. A template has one operation, a sync,
/// so the status says what waits. Every move returns an immutable object and leaves the old one as it was.
public sealed record VmTemplateOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }
    public VmTemplateClusterStatus? LastSeenStatus { get; private init; }

    private VmTemplateOperation() { }

    public static VmTemplateOperation None { get; } = new();

    /// A sync is asked. The last failure stays until a sync ends Done. An empty id would match no report and could never
    /// end it.
    public VmTemplateOperation Asked(Guid id, DateTimeOffset at, VmTemplateClusterStatus? seen)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, AskedAt = at, LastSeenStatus = seen };
    }

    /// The reconciler saw the alias (Available) or did not find it (null).
    public VmTemplateOperation Saw(VmTemplateClusterStatus? status) => this with { LastSeenStatus = status };

    /// The sync ended Done: nothing waits, nothing is held, and the last failure is cleared.
    public VmTemplateOperation Done() => None;

    /// The sync gave up: nothing waits and nothing is held, and the failure is recorded.
    public VmTemplateOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
        this with
        {
            PendingId = null,
            AskedAt = null,
            LastSeenStatus = null,
            FailureReason = reason,
            FailureDetail = detail,
            FailedAt = at,
        };

    /// Nothing waits any more without a sync having ended Done or given up: the last failure stays.
    public VmTemplateOperation ReturnedToRest() => this with { PendingId = null, AskedAt = null, LastSeenStatus = null };
}
