using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Network;

/// What a firewall rule keeps about the operations on its resource on Incus (titan-ops features/network/design.md#network-operation):
/// the one it waits on (its id, which the provider command carries and the report brings back, its kind and when it was
/// asked) and the last one that failed (a fixed reason, the raw detail for admins, and when). Every move returns an
/// immutable object and leaves the old one as it was; the six columns keep the names they had before the object.
public sealed record FirewallRuleOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public FirewallRuleOperationKind? Kind { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }

    private FirewallRuleOperation() { }

    public static FirewallRuleOperation None { get; } = new();

    /// An operation is asked. The last failure stays until an operation ends Done. An empty id would match no report and
    /// could never end it.
    public FirewallRuleOperation Asked(Guid id, FirewallRuleOperationKind kind, DateTimeOffset at)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, Kind = kind, AskedAt = at };
    }

    /// The operation ended Done: nothing waits, and the last failure is cleared.
    public FirewallRuleOperation Done() => None;

    /// The operation gave up: nothing waits, and the failure is recorded.
    public FirewallRuleOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
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
    /// the provider, or one that went with its network): the last failure stays.
    public FirewallRuleOperation ReturnedToRest() => this with { PendingId = null, Kind = null, AskedAt = null };
}
