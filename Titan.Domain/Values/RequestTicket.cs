using Titan.Domain.Exceptions;
using Titan.Domain.Model;

namespace Titan.Domain.Values;

/// What an intent method hands the handler for the command it enqueues: the request's id, which the command's key and
/// payload carry, and the provider id of the resource, which leaves only through a method that validated the state
/// (titan-ops architecture/intent-and-observation.md#aggregate-hands-out-provider-id).
public sealed record RequestTicket : ValueObject
{
    public Guid RequestId { get; private init; }
    public string ProviderId { get; private init; } = default!;

    private RequestTicket() { }

    /// A ticket is for a command against the provider, so it always names the resource there; a request with
    /// nothing on the provider is finished locally and gets no ticket.
    public static RequestTicket Of(Guid requestId, string providerId)
    {
        if (requestId == Guid.Empty)
            throw new BusinessException("INVALID_REQUEST", "A request needs an id.");
        if (string.IsNullOrEmpty(providerId))
            throw new BusinessException("INVALID_REQUEST", "A command against the provider needs the resource's provider id.");
        return new RequestTicket { RequestId = requestId, ProviderId = providerId };
    }
}
