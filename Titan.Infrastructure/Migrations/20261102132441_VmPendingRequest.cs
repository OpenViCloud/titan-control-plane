using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A VM's waiting request becomes three columns — id, kind, when it was asked — written together
    /// (titan-ops architecture/resource-lifecycle.md#shape). The id column is the old pending_intent_id renamed; the kind of
    /// a request already waiting is read from the columns it used to be derived from, in the order Vm.PendingIntent read
    /// them. Written by hand on the scaffold: the scaffold dropped create_finished and has_been_running, which the model
    /// no longer reads, but an image rolled back after this migration still reads them, so they stay with a default
    /// until a release has carried this one (titan-ops architecture/resource-lifecycle.md#rollout, phase 6 drops them).
    /// The view core.pending_requests is what the stuck-request query reads; each later phase adds its table to it.
    public partial class VmPendingRequest : Migration
    {
        /// A delete whose command already gave up stayed waiting before this migration, and the next press reopened its
        /// row. A repeat now sends nothing, so it would wait for ever: it ends here as a Failed would. Same reason text as
        /// VmReports. An open row keeps its delete waiting; its worker reports on the old route.
        private const string EndTheDeletesWhoseCommandFailedSql = @"
UPDATE core.vms v SET
    desired_existence = 'Exists',
    pending_request_id = NULL,
    request_failure_reason = 'The VM could not be deleted. Please try again or contact support.',
    request_failed_at = now()
FROM core.provider_commands c
WHERE v.pending_request_id IS NOT NULL
  AND v.desired_existence = 'Deleted'
  AND c.key = 'vm-delete-' || v.id::text
  AND c.status = 'Failed';";

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
            migrationBuilder.Sql(BackfillTheKindSql);

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
