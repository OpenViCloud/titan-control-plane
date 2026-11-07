using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// A VM snapshot keeps one status (titan-ops features/vm/design.md#vm-data-model): status replaces the existence pair and
    /// the pending kind, and observed_state says whether Incus was last seen holding the snapshot while an operation waits.
    /// Written by hand: the scaffold paired the old and new columns by type and renamed desired_existence to status and
    /// pending_request_kind to observed_state. The view of waiting requests reads the kind off the status, so it is dropped
    /// before the columns go and made again after.
    /// </summary>
    public partial class VmSnapshotSingleStatus : Migration
    {
        // Old columns to the one status, in the order the old snapshot read them: Deleted absorbs, a waiting operation names
        // its move, then what the reconciler last saw. Without a provider id nothing is on Incus, so Failed (the create gave
        // up before Incus had it; the reconciler lists only rows with an id, and Failed deletes at once).
        private const string StatusFromTheOldColumnsSql = @"
UPDATE core.vm_snapshots SET status = CASE
    WHEN current_existence = 'Deleted' THEN 'Deleted'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Delete' THEN 'Deleting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Revert' THEN 'Reverting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Create' THEN 'Creating'
    WHEN external_id IS NULL THEN 'Failed'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    ELSE 'Available'
END;";

        // While an operation waits, whether Incus held the snapshot is kept: billing, quota and the status a revert or delete
        // returns to read it (VmSnapshot.InstanceStatus).
        private const string ObservedStateSql = @"
UPDATE core.vm_snapshots SET observed_state =
    CASE WHEN status IN ('Creating', 'Reverting', 'Deleting') AND current_existence = 'Exists' THEN 'Available' ELSE NULL END;";

        private const string ViewWithSnapshotKindFromStatusSql = @"
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
SELECT 'network', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END, pending_request_asked_at, NULL::text
FROM core.networks
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'firewall-rule', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END, pending_request_asked_at, NULL::text
FROM core.firewall_rules
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'vm-snapshot', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Reverting' THEN 'Revert' WHEN 'Deleting' THEN 'Delete' END,
       pending_request_asked_at, NULL::text
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

        private const string ViewWithSnapshotKindColumnSql = @"
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
SELECT 'network', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END, pending_request_asked_at, NULL::text
FROM core.networks
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'firewall-rule', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END, pending_request_asked_at, NULL::text
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

        // Down puts the old columns back from the status and observed_state. A resting row wanted what it has, so desired is
        // Deleted only while a delete waits or after it; a waiting row exists on Incus when it was last seen there.
        private const string OldColumnsFromTheStatusSql = @"
UPDATE core.vm_snapshots SET
    current_existence = CASE
        WHEN status = 'Deleted' THEN 'Deleted'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN status = 'Available' THEN 'Exists'
        WHEN status = 'Failed' THEN 'NotCreated'
        WHEN status IN ('Creating', 'Reverting', 'Deleting') AND observed_state = 'Available' THEN 'Exists'
        WHEN status = 'Creating' THEN 'NotCreated'
        ELSE 'NotFound' END,
    desired_existence = CASE WHEN status IN ('Deleting', 'Deleted') THEN 'Deleted' ELSE 'Exists' END,
    pending_request_kind = CASE status WHEN 'Creating' THEN 'Create' WHEN 'Reverting' THEN 'Revert' WHEN 'Deleting' THEN 'Delete' END;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.AddColumn<string>(name: "status", schema: "core", table: "vm_snapshots", type: "text",
                nullable: false, defaultValue: "Available");
            migrationBuilder.AddColumn<string>(name: "observed_state", schema: "core", table: "vm_snapshots", type: "text",
                nullable: true);
            migrationBuilder.Sql(StatusFromTheOldColumnsSql);
            migrationBuilder.Sql(ObservedStateSql);
            migrationBuilder.DropColumn(name: "current_existence", schema: "core", table: "vm_snapshots");
            migrationBuilder.DropColumn(name: "desired_existence", schema: "core", table: "vm_snapshots");
            migrationBuilder.DropColumn(name: "pending_request_kind", schema: "core", table: "vm_snapshots");

            migrationBuilder.Sql(ViewWithSnapshotKindFromStatusSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.AddColumn<string>(name: "current_existence", schema: "core", table: "vm_snapshots", type: "text",
                nullable: false, defaultValue: "NotCreated");
            migrationBuilder.AddColumn<string>(name: "desired_existence", schema: "core", table: "vm_snapshots", type: "text",
                nullable: false, defaultValue: "Exists");
            migrationBuilder.AddColumn<string>(name: "pending_request_kind", schema: "core", table: "vm_snapshots", type: "text",
                nullable: true);
            migrationBuilder.Sql(OldColumnsFromTheStatusSql);
            migrationBuilder.DropColumn(name: "status", schema: "core", table: "vm_snapshots");
            migrationBuilder.DropColumn(name: "observed_state", schema: "core", table: "vm_snapshots");

            migrationBuilder.Sql(ViewWithSnapshotKindColumnSql);
        }
    }
}
