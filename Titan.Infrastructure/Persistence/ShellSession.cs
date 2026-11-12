namespace Titan.Infrastructure.Persistence;

/// One open SSH session, so that every control plane replica counts a tenant's sessions alike. Not a domain aggregate:
/// it holds no customer state, only a place under the per-tenant cap (titan-ops features/vm/design.md#ssh-session).
public class ShellSession
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid VmId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    /// Written by the replica holding the session every ShellSessionLimiter.SeenEvery; a row not seen for
    /// ShellSessionLimiter.GoneAfter holds no place.
    public DateTimeOffset SeenAt { get; set; }
}
