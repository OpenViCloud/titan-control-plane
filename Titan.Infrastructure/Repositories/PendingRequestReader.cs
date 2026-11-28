using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

/// Reads core.pending_requests, the view over every lifecycle table's waiting request (vms, volumes, volume_snapshots,
/// networks, firewall_rules, vm_snapshots, load_balancers, vm_template_clusters, vm_key_additions,
/// function_runtime_clusters, functions), and joins the command row whose key the request names: `{resource}-{kind}-{id}-{requestId}`
/// (ProviderCommandQueue.Vm*Key, Volume*Key, Network*Key, FirewallRule*Key, VmSnapshot*Key, LoadBalancer*Key,
/// Function*Key), with the
/// cluster before the request id for the sync of a template or a function runtime (TemplateSyncKey,
/// FunctionRuntimeSyncKey), the two rows of the view that carry a cluster.
public class PendingRequestReader(TitanDbContext db) : IPendingRequestReader
{
    public async Task<IReadOnlyList<StuckRequest>> ListStuckAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - olderThan;
        // A VM stop may wait its stop timeout for the guest before the power is cut, so it is stuck only past the threshold
        // plus that wait (titan-ops features/vm/design.md#vm-stop-timeout). The wait is read from the command row's payload,
        // not from core.vms: the worker waits what the command carries, and the VM's value may have changed since the stop
        // was recorded. A payload without it (enqueued before it was sent) or no row at all counts the worker's default.
        int defaultStopTimeoutSeconds = Vm.DefaultStopTimeoutSeconds;
        // A command that waits while another command of its VM or volume holds the resource (a backup, most often) is not
        // stuck: the worker has not been given it (titan-ops features/provider/design.md#tenant-lane).
        // Quoted aliases: Postgres folds a bare alias to lower case, and SqlQuery maps columns to properties by name.
        List<StuckRequestRow> rows = await db.Database.SqlQuery<StuckRequestRow>($"""
            SELECT p.resource AS "Resource", p.id AS "Id", p.tenant_id AS "TenantId", p.request_id AS "RequestId",
                   p.kind AS "Kind", p.asked_at AS "AskedAt", c.status AS "CommandStatus"
            FROM core.pending_requests p
            LEFT JOIN core.provider_commands c
                   ON c.key = p.resource || '-' || lower(p.kind) || '-' || p.id::text || '-'
                              || coalesce(p.cluster_id || '-', '') || p.request_id::text
            WHERE p.asked_at + make_interval(secs => CASE WHEN p.resource = 'vm' AND p.kind = 'Stop'
                      THEN coalesce((c.payload ->> 'stopTimeoutSeconds')::int, {defaultStopTimeoutSeconds})
                      ELSE 0 END) < {cutoff}
              AND NOT EXISTS (SELECT 1 FROM core.provider_commands b
                              WHERE b.status = 'Leased' AND b.resource_id = c.resource_id AND b.id <> c.id)
            ORDER BY p.asked_at
            """).ToListAsync(ct);
        return rows.Select(r => new StuckRequest(r.Resource, r.Id, r.TenantId, r.RequestId, r.Kind, r.AskedAt, r.CommandStatus)).ToList();
    }

    // Unmapped: SqlQuery materialises it by column name (EF 8+). Settable so the materialiser can write it.
    private sealed class StuckRequestRow
    {
        public string Resource { get; set; } = default!;
        public Guid Id { get; set; }
        public Guid? TenantId { get; set; }
        public Guid RequestId { get; set; }
        public string Kind { get; set; } = default!;
        public DateTimeOffset AskedAt { get; set; }
        public string? CommandStatus { get; set; }
    }
}
