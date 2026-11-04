using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Values;

/// The one operation a resource is waiting on (titan-ops architecture/resource-lifecycle.md#shape): its id, which the
/// provider command carries and the report brings back, its kind, and when it was asked. Only the report with this id
/// ends it (titan-ops architecture/resource-lifecycle.md#invariants). TKind is the resource's own enum of operation kinds.
public sealed record PendingOperation<TKind> : ValueObject where TKind : struct, Enum
{
    public Guid Id { get; private init; }
    public TKind Kind { get; private init; }
    public DateTimeOffset AskedAt { get; private init; }

    private PendingOperation() { }

    /// An empty id would match no report and could never end; refused at the source.
    public static PendingOperation<TKind> Of(Guid id, TKind kind, DateTimeOffset askedAt)
    {
        if (id == Guid.Empty)
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        return new PendingOperation<TKind> { Id = id, Kind = kind, AskedAt = askedAt };
    }
}
