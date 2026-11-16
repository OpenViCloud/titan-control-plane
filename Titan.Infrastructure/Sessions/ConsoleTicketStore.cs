using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Sessions;

// The one-time tickets that open a console websocket (titan-ops features/vm/design.md#vm-console). The client makes the
// ticket, as it makes an API token, so no response carries a secret; Titan keeps its SHA-256 for 30 s.
public sealed partial class ConsoleTicketStore(TitanDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    /// The ticket validator's Matches() copies this string into api/openapi-v1.json as the field's `pattern`.
    public const string TicketPattern = "^ovct_[A-Za-z0-9_-]{43}$";

    public static bool IsWellFormed(string? ticket) => ticket != null && TicketShape().IsMatch(ticket);

    public static string HashOf(string ticket) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    /// A ticket already kept (a client sending the same request again) stays as it was: the request succeeds and keeps
    /// nothing new.
    public async Task IssueAsync(Guid tenantId, Guid vmId, string createdBy, string ticket, CancellationToken ct)
    {
        DateTimeOffset now = clock.GetUtcNow();
        List<ConsoleTicket> expired = await db.ConsoleTickets
            .Where(t => t.TenantId == tenantId && t.ExpiresAt <= now)
            .ToListAsync(ct);
        db.ConsoleTickets.RemoveRange(expired);
        await db.SaveChangesAsync(ct);
        var row = new ConsoleTicket
        {
            Id = Guid.NewGuid(),
            TokenHash = HashOf(ticket),
            TenantId = tenantId,
            VmId = vmId,
            CreatedBy = createdBy,
            ExpiresAt = now + Lifetime,
        };
        await InsertUnlessKeptAsync(row, ct);
    }

    private async Task InsertUnlessKeptAsync(ConsoleTicket row, CancellationToken ct)
    {
        // The in-memory provider of the unit tests runs no SQL and has one caller at a time.
        if (!db.Database.IsRelational())
        {
            bool kept = await db.ConsoleTickets.AnyAsync(t => t.TokenHash == row.TokenHash, ct);
            if (!kept)
            {
                db.ConsoleTickets.Add(row);
                await db.SaveChangesAsync(ct);
            }
            return;
        }
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO core.console_tickets (id, token_hash, tenant_id, vm_id, created_by, expires_at)
            VALUES ({row.Id}, {row.TokenHash}, {row.TenantId}, {row.VmId}, {row.CreatedBy}, {row.ExpiresAt})
            ON CONFLICT (token_hash) DO NOTHING
            """, ct);
    }

    /// The ticket's row, deleted in the same statement so two replicas never both take it; null when no such ticket was
    /// issued, it was taken already, or it is older than Lifetime.
    public async Task<ConsoleTicket?> TakeAsync(string ticket, CancellationToken ct)
    {
        string hash = HashOf(ticket);
        ConsoleTicket? taken = await DeleteAsync(hash, ct);
        if (taken == null)
        {
            return null;
        }
        bool expired = taken.ExpiresAt <= clock.GetUtcNow();
        if (expired)
        {
            return null;
        }
        return taken;
    }

    private async Task<ConsoleTicket?> DeleteAsync(string hash, CancellationToken ct)
    {
        // The in-memory provider of the unit tests runs no SQL; it has one caller at a time, so a find and a remove take
        // the ticket once as well.
        if (!db.Database.IsRelational())
        {
            ConsoleTicket? row = await db.ConsoleTickets.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
            if (row == null)
            {
                return null;
            }
            db.ConsoleTickets.Remove(row);
            await db.SaveChangesAsync(ct);
            return row;
        }
        List<ConsoleTicket> rows = await db.ConsoleTickets
            .FromSqlInterpolated($"DELETE FROM core.console_tickets WHERE token_hash = {hash} RETURNING *")
            .AsNoTracking()
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    [GeneratedRegex(TicketPattern)]
    private static partial Regex TicketShape();
}
