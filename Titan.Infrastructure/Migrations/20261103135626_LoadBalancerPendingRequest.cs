using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A load balancer's waiting request becomes three columns — id, kind, when it was asked — written together
    /// (titan-ops architecture/resource-lifecycle.md#shape). The id column is the old pending_intent_id renamed; the kind of
    /// a request already waiting is read from the columns it used to be derived from, in the order
    /// LoadBalancer.Transition read them before this migration: desired Deleted is a delete, current NotCreated a create,
    /// anything else an update. The rename stops an image from before this migration on every load balancer read:
    /// rolling one back needs this migration's Down first.
    ///
    /// No create waits with a provider id: before this migration the create command's Created report, or its give-up
    /// carrying the VIP, ended it. An open command of the create or the delete takes the request's key and id, so its
    /// report can name the request; an update's key already ends with its intent id, which is the request's id, so an
    /// open update takes that id into its payload, a replaced one included (its report then names a request no longer
    /// waiting). Down leaves the command rows as they are. The view core.pending_requests gains the table.
    public partial class LoadBalancerPendingRequest : Migration
    {
        private const string BackfillTheKindSql = @"
UPDATE core.load_balancers SET
    pending_request_kind = CASE
        WHEN desired_existence = 'Deleted' THEN 'Delete'
        WHEN current_existence = 'NotCreated' THEN 'Create'
        ELSE 'Update'
    END,
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        private const string GiveOpenCreatesAndDeletesTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = c.key || '-' || lb.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', lb.pending_request_id::text)
FROM core.load_balancers lb
WHERE lb.pending_request_kind IN ('Create', 'Delete')
  AND c.status IN ('Pending', 'Leased')
  AND c.key = 'load-balancer-' || lower(lb.pending_request_kind) || '-' || lb.id::text;";

        // `load-balancer-update-{id}-{intentId}`: the last 36 characters are the intent, the request's id.
        private const string GiveOpenUpdatesTheirRequestSql = @"
UPDATE core.provider_commands SET
    payload = payload || jsonb_build_object('requestId', right(key, 36))
WHERE kind = 'load-balancer.update'
  AND status IN ('Pending', 'Leased')
  AND key LIKE 'load-balancer-update-%';";

        private const string ViewWithVmSnapshotsSql = @"
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
WHERE pending_request_id IS NOT NULL;";

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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "load_balancers",
                newName: "pending_request_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "load_balancers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "load_balancers",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(BackfillTheKindSql);
            migrationBuilder.Sql(GiveOpenCreatesAndDeletesTheirRequestSql);
            migrationBuilder.Sql(GiveOpenUpdatesTheirRequestSql);
            migrationBuilder.Sql(ViewWithLoadBalancersSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view reads the kind column: back to the tables of rollout phases 1 to 4 before it goes.
            migrationBuilder.Sql(ViewWithVmSnapshotsSql);

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "load_balancers");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "load_balancers");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "load_balancers",
                newName: "pending_intent_id");
        }
    }
}
