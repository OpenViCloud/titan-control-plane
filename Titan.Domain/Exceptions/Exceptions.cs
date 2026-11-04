namespace Titan.Domain.Exceptions;

public class NotFoundException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public class ConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// A call lost a race. A SaveChanges lost a concurrency-token or unique guard: Infrastructure translates EF's
/// DbUpdateConcurrencyException / unique violation into this so Application can retry or re-read without
/// referencing EF. Or a domain service read what another call already did (InvoiceWalletPayment); its caller answers
/// it, and one that does not sends the code out as a 409 (ErrorStatus).
public class ConcurrencyConflictException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public class BusinessException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
