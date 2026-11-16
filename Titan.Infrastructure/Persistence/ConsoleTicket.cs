namespace Titan.Infrastructure.Persistence;

/// A ticket that opens one console websocket (titan-ops features/vm/design.md#vm-console). Not a domain aggregate: a
/// browser cannot put a token on a websocket, so the ticket stands in for it for 30 s. Only the SHA-256 of the ticket is
/// kept.
public class ConsoleTicket
{
    public Guid Id { get; set; }
    public string TokenHash { get; set; } = "";
    public Guid TenantId { get; set; }
    public Guid VmId { get; set; }
    /// The `sub` of whoever took the ticket: the console session acts, and is recorded, as them.
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}
