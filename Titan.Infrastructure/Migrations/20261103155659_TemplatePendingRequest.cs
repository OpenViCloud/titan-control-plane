using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A template's waiting sync in one cluster becomes three columns — id, kind, when it was asked — written together
    /// (titan-ops architecture/resource-lifecycle.md#shape). The id column is the old pending_intent_id renamed; a sync
    /// already waiting gets the one kind there is. The rename stops an image from before this migration on every template
    /// read: rolling one back needs this migration's Down first.
    ///
    /// An open sync command of a cluster that waits takes the request's id into its key and payload, so its report can
    /// name the request; a finished one, and one of a cluster with nothing waiting, are left as they are. Down leaves
    /// the command rows as they are. The view core.pending_requests gains the table, with no tenant.
    public partial class TemplatePendingRequest : Migration
    {
        private const string BackfillTheKindSql = @"
UPDATE core.vm_template_clusters SET
    pending_request_kind = 'Sync',
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        // `template-sync-{templateId}-{clusterId}`: the key before this migration, one per template per cluster.
        private const string GiveOpenSyncsTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = c.key || '-' || tc.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', tc.pending_request_id::text)
FROM core.vm_template_clusters tc
WHERE tc.pending_request_id IS NOT NULL
  AND c.status IN ('Pending', 'Leased')
  AND c.key = 'template-sync-' || tc.template_id::text || '-' || tc.cluster_id;";

        private const string ViewWithLoadBalancersSql = @"
CREATE OR REPLACE VIEW core.pending_requests AS
SELECT 'vm' AS resource, id, tenant_id, pending_request_id AS request_id, pending_request_kind AS kind,
       pending_request_asked_at AS asked_at
FROM core.vms
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'volume', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at
FROM core.volumes
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'volume-snapshot', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at
FROM core.volume_snapshots
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'network', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at
FROM core.networks
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'firewall-rule', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at
FROM core.firewall_rules
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'vm-snapshot', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at
FROM core.vm_snapshots
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'load-balancer', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at
FROM core.load_balancers
WHERE pending_request_id IS NOT NULL;";

        // A template has no tenant, and its command's key names the cluster: the view carries the cluster for the stuck
        // query to rebuild the key (PendingRequestReader). A new column goes last: CREATE OR REPLACE keeps the others.
        private const string ViewWithTemplatesSql = @"
CREATE OR REPLACE VIEW core.pending_requests AS
SELECT 'vm' AS resource, id, tenant_id, pending_request_id AS request_id, pending_request_kind AS kind,
       pending_request_asked_at AS asked_at, NULL::text AS cluster_id
FROM core.vms
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'volume', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at, NULL::text
FROM core.volumes
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'volume-snapshot', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at, NULL::text
FROM core.volume_snapshots
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'network', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at, NULL::text
FROM core.networks
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'firewall-rule', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at, NULL::text
FROM core.firewall_rules
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'vm-snapshot', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at, NULL::text
FROM core.vm_snapshots
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'load-balancer', id, tenant_id, pending_request_id, pending_request_kind, pending_request_asked_at, NULL::text
FROM core.load_balancers
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'template', template_id, NULL::uuid, pending_request_id, pending_request_kind, pending_request_asked_at, cluster_id
FROM core.vm_template_clusters
WHERE pending_request_id IS NOT NULL;";

        // CREATE OR REPLACE cannot drop the cluster column.
        private const string DropTheViewSql = "DROP VIEW core.pending_requests;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "vm_template_clusters",
                newName: "pending_request_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "vm_template_clusters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "vm_template_clusters",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(BackfillTheKindSql);
            migrationBuilder.Sql(GiveOpenSyncsTheirRequestSql);
            migrationBuilder.Sql(ViewWithTemplatesSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view reads the kind column: back to the tables of the earlier phases before it goes.
            migrationBuilder.Sql(DropTheViewSql);
            migrationBuilder.Sql(ViewWithLoadBalancersSql);

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "vm_template_clusters");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "vm_template_clusters");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "vm_template_clusters",
                newName: "pending_intent_id");
        }
    }
}
