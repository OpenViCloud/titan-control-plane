using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Values;

/// What an intent method hands the handler for the command it enqueues: the operation's id, which the command's key and
/// payload carry, and the provider id of the resource, which leaves only through a method that validated the state
/// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public sealed record OperationTicket : ValueObject
{
    public Guid OperationId { get; private init; }
    public string ProviderId { get; private init; } = default!;

    private OperationTicket() { }

    /// A ticket is for a command against the provider, so it always names the resource there; an operation with
    /// nothing on the provider is finished locally and gets no ticket.
    public static OperationTicket Of(Guid operationId, string providerId)
    {
        if (operationId == Guid.Empty)
            throw new BusinessException("INVALID_REQUEST", "An operation needs an id.");
        if (string.IsNullOrEmpty(providerId))
            throw new BusinessException("INVALID_REQUEST", "A command against the provider needs the resource's provider id.");
        return new OperationTicket { OperationId = operationId, ProviderId = providerId };
    }
}
