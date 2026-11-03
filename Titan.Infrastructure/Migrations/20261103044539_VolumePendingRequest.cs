using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A volume's and a volume snapshot's waiting request become three columns each — id, kind, when it was asked —
    /// written together (titan-ops architecture/resource-lifecycle.md#shape). The id column is the old pending_intent_id
    /// renamed; the kind of a request already waiting is read from the columns it used to be derived from, in the order
    /// Volume.Transition and VolumeSnapshot.Transition read them before this migration. The rename stops an image from
    /// before this migration on every volume read: rolling one back needs this migration's Down first.
    ///
    /// Requests under way meet the new rule here: a detach with no open command ends — the one a VM's delete asked for
    /// had none and is no longer a request (titan-ops architecture/resource-lifecycle.md#kinds), and a customer's whose
    /// command closed before an observation ended it has no report coming either; any other request without an open
    /// command keeps waiting and shows in the stuck query. A command still open takes the request's key and id, so its
    /// report can name the request; a restore's is keyed as the new volume's create. Down leaves the command rows as they
    /// are.
    /// The view core.pending_requests gains the two tables.
    public partial class VolumePendingRequest : Migration
    {
        private const string BackfillTheVolumeKindSql = @"
UPDATE core.volumes SET
    pending_request_kind = CASE
        WHEN desired_existence = 'Deleted' THEN 'Delete'
        WHEN current_existence = 'NotCreated' THEN 'Create'
        WHEN desired_attached_vm_id IS NOT NULL THEN 'Attach'
        ELSE 'Detach'
    END,
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        private const string BackfillTheSnapshotKindSql = @"
UPDATE core.volume_snapshots SET
    pending_request_kind = CASE WHEN desired_existence = 'Deleted' THEN 'Delete' ELSE 'Create' END,
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        /// A detach with no open command has no report coming: the one Volume.DetachWithDeletedVm left never had one, and
        /// a customer's whose command closed was waiting for an observation that no longer ends it. The desired attachment
        /// stays as asked, which is what a VM's delete now leaves on a volume with nothing waiting.
        private const string EndTheDetachesWithoutAnOpenCommandSql = @"
UPDATE core.volumes v SET pending_request_id = NULL, pending_request_kind = NULL, pending_request_asked_at = NULL
WHERE v.pending_request_kind = 'Detach'
  AND NOT EXISTS (
      SELECT 1 FROM core.provider_commands c
      WHERE c.key = 'volume-detach-' || v.id::text AND c.status IN ('Pending', 'Leased'));";

        private const string GiveOpenVolumeCommandsTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = 'volume-' || lower(v.pending_request_kind) || '-' || v.id::text || '-' || v.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', v.pending_request_id::text)
FROM core.volumes v
WHERE v.pending_request_id IS NOT NULL
  AND c.status IN ('Pending', 'Leased')
  AND (c.key = 'volume-' || lower(v.pending_request_kind) || '-' || v.id::text
       OR (v.pending_request_kind = 'Create' AND c.key = 'volume-snapshot-restore-' || v.id::text));";

        private const string GiveOpenSnapshotCommandsTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = 'volume-snapshot-' || lower(s.pending_request_kind) || '-' || s.id::text || '-' || s.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', s.pending_request_id::text)
FROM core.volume_snapshots s
WHERE s.pending_request_id IS NOT NULL
  AND c.status IN ('Pending', 'Leased')
  AND c.key = 'volume-snapshot-' || lower(s.pending_request_kind) || '-' || s.id::text;";

        private const string VmOnlyViewSql = @"
CREATE OR REPLACE VIEW core.pending_requests AS
SELECT 'vm' AS resource, id, tenant_id, pending_request_id AS request_id, pending_request_kind AS kind,
       pending_request_asked_at AS asked_at
FROM core.vms
WHERE pending_request_id IS NOT NULL;";

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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "volumes",
                newName: "pending_request_id");

            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "volume_snapshots",
                newName: "pending_request_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "volumes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "volumes",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "volume_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "volume_snapshots",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(BackfillTheVolumeKindSql);
            migrationBuilder.Sql(BackfillTheSnapshotKindSql);
            migrationBuilder.Sql(EndTheDetachesWithoutAnOpenCommandSql);
            migrationBuilder.Sql(GiveOpenVolumeCommandsTheirRequestSql);
            migrationBuilder.Sql(GiveOpenSnapshotCommandsTheirRequestSql);
            migrationBuilder.Sql(ViewWithVolumesSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The view reads the kind columns: back to the VM alone before they go.
            migrationBuilder.Sql(VmOnlyViewSql);

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "volumes");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "volumes");

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "volume_snapshots");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "volume_snapshots");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "volumes",
                newName: "pending_intent_id");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "volume_snapshots",
                newName: "pending_intent_id");
        }
    }
}
