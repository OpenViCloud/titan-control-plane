using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.Network;

/// What a firewall rule keeps about the operations on its line in the ACL on Incus (titan-ops
/// features/network/design.md#network-operation): the one it waits on (its id, which the provider command carries and the
/// report brings back, and when it was asked), the last one that failed (a fixed reason, the raw detail for admins, and
/// when), and the Incus state seen while an operation waits, null for a rule at rest. What the operation is, a create
/// or a delete, is the rule's status. Every move returns an immutable object and leaves the old one as it was.
public sealed record FirewallRuleOperation : ValueObject
{
    public Guid? PendingId { get; private init; }
    public DateTimeOffset? AskedAt { get; private init; }
    public string? FailureReason { get; private init; }
    public string? FailureDetail { get; private init; }
    public DateTimeOffset? FailedAt { get; private init; }
    public FirewallRuleStatus? LastSeenStatus { get; private init; }

    private FirewallRuleOperation() { }

    public static FirewallRuleOperation None { get; } = new();

    /// An operation is asked. The last failure stays until an operation ends Done. An empty id would match no report and
    /// could never end it.
    public FirewallRuleOperation Asked(Guid id, DateTimeOffset at, FirewallRuleStatus? seen)
    {
        if (id == Guid.Empty)
        {
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        }
        return this with { PendingId = id, AskedAt = at, LastSeenStatus = seen };
    }

    /// The reconciler saw this Incus state (null: none, the rule was not found).
    public FirewallRuleOperation Saw(FirewallRuleStatus? status) => this with { LastSeenStatus = status };

    /// The operation ended Done: nothing waits, nothing is held, and the last failure is cleared.
    public FirewallRuleOperation Done() => None;

    /// The operation gave up: nothing waits and no status is held, and the failure is recorded.
    public FirewallRuleOperation Failed(string? reason, string? detail, DateTimeOffset at) =>
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
    /// the provider, or an operation that went with its network): the last failure stays.
    public FirewallRuleOperation ReturnedToRest() => this with { PendingId = null, AskedAt = null, LastSeenStatus = null };
}
