using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Reads core.pending_requests, the view over every lifecycle table's waiting request (today: vms, volumes,
/// volume_snapshots, networks, firewall_rules, vm_snapshots, load_balancers; each rollout phase adds its table —
/// titan-ops architecture/resource-lifecycle.md#rollout), and joins the command row whose key the request names:
/// `{resource}-{kind}-{id}-{requestId}` (ProviderCommandQueue.Vm*Key, Volume*Key, Network*Key, FirewallRule*Key,
/// VmSnapshot*Key, LoadBalancer*Key).
public class PendingRequestReader(TitanDbContext db) : IPendingRequestReader
{
    public async Task<IReadOnlyList<StuckRequest>> ListStuckAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - olderThan;
        // Quoted aliases: Postgres folds a bare alias to lower case, and SqlQuery maps columns to properties by name.
        List<StuckRequestRow> rows = await db.Database.SqlQuery<StuckRequestRow>($"""
            SELECT p.resource AS "Resource", p.id AS "Id", p.tenant_id AS "TenantId", p.request_id AS "RequestId",
                   p.kind AS "Kind", p.asked_at AS "AskedAt", c.status AS "CommandStatus"
            FROM core.pending_requests p
            LEFT JOIN core.provider_commands c
                   ON c.key = p.resource || '-' || lower(p.kind) || '-' || p.id::text || '-' || p.request_id::text
            WHERE p.asked_at < {cutoff}
            ORDER BY p.asked_at
            """).ToListAsync(ct);
        return rows.Select(r => new StuckRequest(r.Resource, r.Id, r.TenantId, r.RequestId, r.Kind, r.AskedAt, r.CommandStatus)).ToList();
    }

    // Unmapped: SqlQuery materialises it by column name (EF 8+). Settable so the materialiser can write it.
    private sealed class StuckRequestRow
    {
        public string Resource { get; set; } = default!;
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid RequestId { get; set; }
        public string Kind { get; set; } = default!;
        public DateTimeOffset AskedAt { get; set; }
        public string? CommandStatus { get; set; }
    }
}
