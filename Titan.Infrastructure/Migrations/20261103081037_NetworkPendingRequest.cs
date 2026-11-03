using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A network's and a firewall rule's waiting request become three columns each — id, kind, when it was asked —
    /// written together (titan-ops architecture/resource-lifecycle.md#shape). The id column is the old pending_intent_id
    /// renamed; the kind of a request already waiting is read from the column it used to be derived from, as
    /// Network.Transition and FirewallRule.Transition read it before this migration. The rename stops an image from
    /// before this migration on every network read: rolling one back needs this migration's Down first.
    ///
    /// Requests under way meet the new rule here: a create whose command already reported the provider id was waiting
    /// for the reconciler, and an observation no longer ends it (titan-ops architecture/resource-lifecycle.md#invariants),
    /// so it ends; its outcome is not recorded. A command still open takes the request's key and id, so its report can
    /// name the request; the rule's keys move from `firewall-` to `firewall-rule-`, the resource name the stuck query
    /// joins on. Down leaves the command rows as they are.
    /// The view core.pending_requests gains the two tables.
    public partial class NetworkPendingRequest : Migration
    {
        private const string BackfillTheNetworkKindSql = @"
UPDATE core.networks SET
    pending_request_kind = CASE WHEN desired_existence = 'Deleted' THEN 'Delete' ELSE 'Create' END,
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        private const string BackfillTheRuleKindSql = @"
UPDATE core.firewall_rules SET
    pending_request_kind = CASE WHEN desired_existence = 'Deleted' THEN 'Delete' ELSE 'Create' END,
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        /// The create's command already reported the provider id; before this migration an observation ended it (a
        /// network's Created, Errored, Unavailable or not found; any reading of a rule), and none does now.
        private const string EndTheCreatesTheirCommandReportedSql = @"
UPDATE core.networks SET pending_request_id = NULL, pending_request_kind = NULL, pending_request_asked_at = NULL
WHERE pending_request_kind = 'Create' AND external_id IS NOT NULL;
UPDATE core.firewall_rules SET pending_request_id = NULL, pending_request_kind = NULL, pending_request_asked_at = NULL
WHERE pending_request_kind = 'Create' AND external_id IS NOT NULL;";

        private const string GiveOpenNetworkCommandsTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = 'network-' || lower(n.pending_request_kind) || '-' || n.id::text || '-' || n.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', n.pending_request_id::text)
FROM core.networks n
WHERE n.pending_request_id IS NOT NULL
  AND c.status IN ('Pending', 'Leased')
  AND c.key = 'network-' || lower(n.pending_request_kind) || '-' || n.id::text;";

        private const string GiveOpenRuleCommandsTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = 'firewall-rule-' || lower(r.pending_request_kind) || '-' || r.id::text || '-' || r.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', r.pending_request_id::text)
FROM core.firewall_rules r
WHERE r.pending_request_id IS NOT NULL
  AND c.status IN ('Pending', 'Leased')
  AND c.key = 'firewall-' || lower(r.pending_request_kind) || '-' || r.id::text;";

        private const string ViewWithVolumesSql = @"
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
WHERE pending_request_id IS NOT NULL;";

        private const string ViewWithNetworksSql = @"
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
WHERE pending_request_id IS NOT NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "networks",
                newName: "pending_request_id");

            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "firewall_rules",
                newName: "pending_request_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "networks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "networks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "firewall_rules",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "firewall_rules",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(BackfillTheNetworkKindSql);
            migrationBuilder.Sql(BackfillTheRuleKindSql);
            migrationBuilder.Sql(EndTheCreatesTheirCommandReportedSql);
            migrationBuilder.Sql(GiveOpenNetworkCommandsTheirRequestSql);
            migrationBuilder.Sql(GiveOpenRuleCommandsTheirRequestSql);
            migrationBuilder.Sql(ViewWithNetworksSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view reads the kind columns: back to the tables of rollout phases 1 and 2 before they go.
            migrationBuilder.Sql(ViewWithVolumesSql);

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "firewall_rules");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "firewall_rules");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "networks",
                newName: "pending_intent_id");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "firewall_rules",
                newName: "pending_intent_id");
        }
    }
}
