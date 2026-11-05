using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// The VM keeps one status (titan-ops features/vm/design.md#vm-status): status replaces the desired and existence
    /// pairs and the pending kind, current_state becomes observed_state. Written by hand: the scaffold paired the old and
    /// new columns by type and renamed desired_state to status. The view of waiting requests reads the VM's kind off its
    /// status, so it is dropped before the columns go and made again after.
    /// </summary>
    public partial class VmSingleStatus : Migration
    {
        // Old columns to the one status, in the order the old aggregate read them: Deleted absorbs, a waiting request
        // names its move, then what the reconciler last saw. A combination none of these names becomes Failed when no
        // instance was ever named (the reconciler lists only VMs with a provider id, and Failed deletes at once), else
        // Unknown, which the reconciler's next cycle replaces (Tôn Chủ accepts losing VM data in beta).
        private const string StatusFromTheOldColumnsSql = @"
UPDATE core.vms SET status = CASE
    WHEN current_existence = 'Deleted' THEN 'Deleted'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Create' THEN 'Creating'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Start' THEN 'Starting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Stop' THEN 'Stopping'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Delete' THEN 'Deleting'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    WHEN current_existence = 'NotCreated' AND request_failure_reason IS NOT NULL THEN 'Failed'
    WHEN current_existence = 'Exists' AND current_state IS NOT NULL THEN current_state
    WHEN external_id IS NULL THEN 'Failed'
    ELSE 'Unknown'
END;";

        private const string ViewWithVmKindFromStatusSql = @"
CREATE VIEW core.pending_requests AS
SELECT 'vm' AS resource, id, tenant_id, pending_request_id AS request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Starting' THEN 'Start'
                   WHEN 'Stopping' THEN 'Stop' WHEN 'Deleting' THEN 'Delete' END AS kind,
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

        private const string ViewWithVmKindColumnSql = @"
CREATE VIEW core.pending_requests AS
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

        // Down puts the old columns back from the status (observed_state is current_state again by then); desired follows the status, a resting VM wanted what it has.
        private const string OldColumnsFromTheStatusSql = @"
UPDATE core.vms SET
    current_existence = CASE
        WHEN status = 'Deleted' THEN 'Deleted'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN external_id IS NULL THEN 'NotCreated'
        ELSE 'Exists' END,
    desired_existence = CASE WHEN status IN ('Deleting', 'Deleted') THEN 'Deleted' ELSE 'Exists' END,
    desired_state = CASE WHEN status IN ('Stopping', 'Stopped') THEN 'Stopped' ELSE 'Running' END,
    pending_request_kind = CASE status WHEN 'Creating' THEN 'Create' WHEN 'Starting' THEN 'Start'
                                       WHEN 'Stopping' THEN 'Stop' WHEN 'Deleting' THEN 'Delete' END;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");
            migrationBuilder.DropIndex(name: "IX_vms_tenant_id_name", schema: "core", table: "vms");

            migrationBuilder.AddColumn<string>(name: "status", schema: "core", table: "vms", type: "text",
                nullable: false, defaultValue: "Unknown");
            migrationBuilder.Sql(StatusFromTheOldColumnsSql);
            migrationBuilder.RenameColumn(name: "current_state", schema: "core", table: "vms", newName: "observed_state");
            migrationBuilder.DropColumn(name: "desired_state", schema: "core", table: "vms");
            migrationBuilder.DropColumn(name: "current_existence", schema: "core", table: "vms");
            migrationBuilder.DropColumn(name: "desired_existence", schema: "core", table: "vms");
            migrationBuilder.DropColumn(name: "pending_request_kind", schema: "core", table: "vms");

            migrationBuilder.CreateIndex(name: "IX_vms_tenant_id_name", schema: "core", table: "vms",
                columns: new[] { "tenant_id", "name" }, unique: true, filter: "status <> 'Deleted'");
            migrationBuilder.Sql(ViewWithVmKindFromStatusSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");
            migrationBuilder.DropIndex(name: "IX_vms_tenant_id_name", schema: "core", table: "vms");

            migrationBuilder.RenameColumn(name: "observed_state", schema: "core", table: "vms", newName: "current_state");
            migrationBuilder.AddColumn<string>(name: "desired_state", schema: "core", table: "vms", type: "text",
                nullable: false, defaultValue: "Running");
            migrationBuilder.AddColumn<string>(name: "current_existence", schema: "core", table: "vms", type: "text",
                nullable: false, defaultValue: "NotCreated");
            migrationBuilder.AddColumn<string>(name: "desired_existence", schema: "core", table: "vms", type: "text",
                nullable: false, defaultValue: "Exists");
            migrationBuilder.AddColumn<string>(name: "pending_request_kind", schema: "core", table: "vms", type: "text",
                nullable: true);
            migrationBuilder.Sql(OldColumnsFromTheStatusSql);
            migrationBuilder.DropColumn(name: "status", schema: "core", table: "vms");

            migrationBuilder.CreateIndex(name: "IX_vms_tenant_id_name", schema: "core", table: "vms",
                columns: new[] { "tenant_id", "name" }, unique: true, filter: "current_existence <> 'Deleted'");
            migrationBuilder.Sql(ViewWithVmKindColumnSql);
        }
    }
}
