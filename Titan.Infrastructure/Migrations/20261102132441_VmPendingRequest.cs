using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A VM's waiting request becomes three columns — id, kind, when it was asked — written together
    /// (titan-ops architecture/resource-lifecycle.md#shape). The id column is the old pending_intent_id renamed; the kind of
    /// a request already waiting is read from the columns it used to be derived from, in the order Vm.PendingIntent read
    /// them. Written by hand on the scaffold: the scaffold dropped create_finished and has_been_running, which the model
    /// no longer reads; kept, with a default for the inserts of a model that does not write them, until a release has
    /// carried this one (titan-ops architecture/resource-lifecycle.md#rollout, phase 6 drops them). The rename stops an image
    /// from before this migration on every VM read: rolling one back needs this migration's Down first.
    ///
    /// Requests under way meet the new rule here, so none is left with nothing to end it: a delete whose command gave up
    /// and a create whose instance name already arrived end now, and a command still open takes the request's key and
    /// id, so its report can name the request. Down leaves those rows as they are: they run whatever their key.
    /// The view core.pending_requests is what the stuck-request query reads; each later phase adds its table to it.
    public partial class VmPendingRequest : Migration
    {
        /// A delete whose command already gave up stayed waiting before this migration, and the next press reopened its
        /// row. A repeat now sends nothing, so it would wait for ever: it ends as a Failed does, with no activity row because
        /// no report arrived. Same reason text as VmReports. A detail of an older failure does not stay beside it.
        private const string EndTheDeletesWhoseCommandFailedSql = @"
UPDATE core.vms v SET
    desired_existence = 'Exists',
    pending_request_id = NULL,
    request_failure_reason = 'The VM could not be deleted. Please try again or contact support.',
    request_failure_detail = NULL,
    request_failed_at = now()
FROM core.provider_commands c
WHERE v.pending_request_id IS NOT NULL
  AND v.desired_existence = 'Deleted'
  AND c.key = 'vm-delete-' || v.id::text
  AND c.status = 'Failed';";

        /// The old create command reports the instance name, then the reconciler's Running ended the create. An observation
        /// no longer ends one and that report will not come again; it was the create's Done
        /// (titan-ops architecture/resource-lifecycle.md#kinds). The vm.created it would have recorded is not written.
        private const string EndTheCreatesWhoseNameArrivedSql = @"
UPDATE core.vms SET pending_request_id = NULL
WHERE pending_request_id IS NOT NULL
  AND desired_existence = 'Exists'
  AND desired_state = 'Running'
  AND create_finished = false
  AND external_id IS NOT NULL;";

        private const string BackfillTheKindSql = @"
UPDATE core.vms SET
    pending_request_kind = CASE
        WHEN desired_existence = 'Deleted' THEN 'Delete'
        WHEN desired_state = 'Stopped' THEN 'Stop'
        WHEN create_finished = false THEN 'Create'
        ELSE 'Start'
    END,
    pending_request_asked_at = now()
WHERE pending_request_id IS NOT NULL;";

        /// A stop or start already has the key, its request id being the old intent id; only the id joins its payload.
        private const string GiveOpenCommandsTheirRequestSql = @"
UPDATE core.provider_commands c SET
    key = 'vm-' || lower(v.pending_request_kind) || '-' || v.id::text || '-' || v.pending_request_id::text,
    payload = c.payload || jsonb_build_object('requestId', v.pending_request_id::text)
FROM core.vms v
WHERE v.pending_request_id IS NOT NULL
  AND c.status IN ('Pending', 'Leased')
  AND c.key = CASE v.pending_request_kind
      WHEN 'Create' THEN 'vm-provision-' || v.id::text
      WHEN 'Delete' THEN 'vm-delete-' || v.id::text
      ELSE 'vm-' || lower(v.pending_request_kind) || '-' || v.id::text || '-' || v.pending_request_id::text
  END;";

        private const string CreateTheViewSql = @"
CREATE VIEW core.pending_requests AS
SELECT 'vm' AS resource, id, tenant_id, pending_request_id AS request_id, pending_request_kind AS kind,
       pending_request_asked_at AS asked_at
FROM core.vms
WHERE pending_request_id IS NOT NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "pending_intent_id",
                schema: "core",
                table: "vms",
                newName: "pending_request_id");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "pending_request_asked_at",
                schema: "core",
                table: "vms",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pending_request_kind",
                schema: "core",
                table: "vms",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(EndTheDeletesWhoseCommandFailedSql);
            migrationBuilder.Sql(EndTheCreatesWhoseNameArrivedSql);
            migrationBuilder.Sql(BackfillTheKindSql);
            migrationBuilder.Sql(GiveOpenCommandsTheirRequestSql);

            // Kept, not dropped: NOT NULL with no default would refuse every insert from a model that no longer
            // writes them.
            migrationBuilder.AlterColumn<bool>(
                name: "create_finished",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AlterColumn<bool>(
                name: "has_been_running",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.Sql(CreateTheViewSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.AlterColumn<bool>(
                name: "has_been_running",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AlterColumn<bool>(
                name: "create_finished",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.DropColumn(
                name: "pending_request_asked_at",
                schema: "core",
                table: "vms");

            migrationBuilder.DropColumn(
                name: "pending_request_kind",
                schema: "core",
                table: "vms");

            migrationBuilder.RenameColumn(
                name: "pending_request_id",
                schema: "core",
                table: "vms",
                newName: "pending_intent_id");
        }
    }
}
