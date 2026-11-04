using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// A delete that never reached the provider happens in RequestDelete itself and returns no ticket
// (titan-ops architecture/desired-and-current.md#delete-locally). Anything else either refuses or asks the provider.
internal static class LocalDelete
{
    public static bool DeletedHere(Func<RequestTicket?> requestDelete)
    {
        try
        {
            return requestDelete() == null;
        }
        catch (BusinessException)
        {
            return false;
        }
    }
}
