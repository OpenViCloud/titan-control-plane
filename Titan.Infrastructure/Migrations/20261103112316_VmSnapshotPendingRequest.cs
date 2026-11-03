using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A VM snapshot's waiting request becomes three columns — id, kind, when it was asked — written together
    /// (titan-ops architecture/resource-lifecycle.md#shape). The id column is the old pending_intent_id renamed; the kind of a
    /// request already waiting is read from the columns it used to be derived from, in the order VmSnapshot.Transition
    /// read them before this migration: desired Deleted is a delete, current NotCreated a create, anything else a revert.
    /// The rename stops an image from before this migration on every VM read (the snapshots load with the VM): rolling one
    /// back needs this migration's Down first.
    ///
    /// No create waits with a provider id: before this migration the create command's Created report ended it. A command
    /// still open takes the request's key and id, so its report can name the request. Down leaves the command rows as they
    /// are. The view core.pending_requests gains the table.
    public partial class VmSnapshotPendingRequest : Migration
    {
        private const string BackfillTheKindSql = @"
UPDATE core.vm_snapshots SET
    pending_request_kind = CASE
        WHEN desired_existence = 'Deleted' THEN 'Delete'
        WHEN current_existence = 'NotCreated' THEN 'Create'
        ELSE 'Revert'
    END,
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        private const string GiveOpenCommandsTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = c.key || '-' || s.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', s.pending_request_id::text)
FROM core.vm_snapshots s
WHERE s.pending_request_id IS NOT NULL
  AND c.status IN ('Pending', 'Leased')
  AND c.key = 'vm-snapshot-' || lower(s.pending_request_kind) || '-' || s.id::text;";

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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "vm_snapshots",
                newName: "pending_request_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "vm_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "vm_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(BackfillTheKindSql);
            migrationBuilder.Sql(GiveOpenCommandsTheirRequestSql);
            migrationBuilder.Sql(ViewWithVmSnapshotsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view reads the kind column: back to the tables of rollout phases 1 to 3 before it goes.
            migrationBuilder.Sql(ViewWithNetworksSql);

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "vm_snapshots");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "vm_snapshots");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "vm_snapshots",
                newName: "pending_intent_id");
        }
    }
}
