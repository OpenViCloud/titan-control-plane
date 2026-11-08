using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// A volume and a volume snapshot keep one status each (titan-ops features/volume/design.md#volume-data-model): status
    /// replaces the existence pair and the pending kind, and observed_state says whether Incus was last seen holding the
    /// resource while an operation waits. The attachment columns of a volume stay. Written by hand: the scaffold paired the
    /// old and new columns by type and renamed desired_existence to status and pending_request_kind to observed_state. The
    /// view of waiting requests reads the kind off the status, so it is dropped before the columns go and made again after.
    /// </summary>
    public partial class VolumeSingleStatus : Migration
    {
        // Old columns to the one status, in the order the old aggregate read them: Deleted absorbs, a waiting operation names
        // its move, then what the reconciler last saw. Without a provider id nothing is on Incus, so Failed (the create gave
        // up before Incus had it; the reconciler lists only rows with an id, and Failed deletes at once).
        private const string VolumeStatusFromTheOldColumnsSql = @"
UPDATE core.volumes SET status = CASE
    WHEN current_existence = 'Deleted' THEN 'Deleted'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Delete' THEN 'Deleting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Attach' THEN 'Attaching'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Detach' THEN 'Detaching'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Create' THEN 'Creating'
    WHEN external_id IS NULL THEN 'Failed'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    ELSE 'Available'
END;";

        private const string SnapshotStatusFromTheOldColumnsSql = @"
UPDATE core.volume_snapshots SET status = CASE
    WHEN current_existence = 'Deleted' THEN 'Deleted'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Delete' THEN 'Deleting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Create' THEN 'Creating'
    WHEN external_id IS NULL THEN 'Failed'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    ELSE 'Available'
END;";

        // While an operation waits, whether Incus held the resource is kept: billing, quota and the status a failed
        // operation returns to read it (Volume.InstanceStatus, VolumeSnapshot.InstanceStatus).
        private const string VolumeObservedStateSql = @"
UPDATE core.volumes SET observed_state =
    CASE WHEN status IN ('Creating', 'Attaching', 'Detaching', 'Deleting') AND current_existence = 'Exists' THEN 'Available' ELSE NULL END;";

        private const string SnapshotObservedStateSql = @"
UPDATE core.volume_snapshots SET observed_state =
    CASE WHEN status IN ('Creating', 'Deleting') AND current_existence = 'Exists' THEN 'Available' ELSE NULL END;";

        private const string ViewWithStorageKindFromStatusSql = @"
CREATE VIEW core.pending_requests AS
SELECT 'vm' AS resource, id, tenant_id, pending_request_id AS request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Starting' THEN 'Start'
                   WHEN 'Stopping' THEN 'Stop' WHEN 'Deleting' THEN 'Delete' END AS kind,
       pending_request_asked_at AS asked_at, NULL::text AS cluster_id
FROM core.vms
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'volume', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Attaching' THEN 'Attach'
                   WHEN 'Detaching' THEN 'Detach' WHEN 'Deleting' THEN 'Delete' END,
       pending_request_asked_at, NULL::text
FROM core.volumes
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'volume-snapshot', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END, pending_request_asked_at, NULL::text
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

        private const string ViewWithStorageKindColumnSql = @"
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

        // Down puts the old columns back from the status and observed_state. A resting row wanted what it has, so desired is
        // Deleted only while a delete waits or after it; a waiting row exists on Incus when it was last seen there.
        private const string OldVolumeColumnsFromTheStatusSql = @"
UPDATE core.volumes SET
    current_existence = CASE
        WHEN status = 'Deleted' THEN 'Deleted'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN status = 'Available' THEN 'Exists'
        WHEN status = 'Failed' THEN 'NotCreated'
        WHEN status IN ('Creating', 'Attaching', 'Detaching', 'Deleting') AND observed_state = 'Available' THEN 'Exists'
        WHEN status = 'Creating' THEN 'NotCreated'
        ELSE 'NotFound' END,
    desired_existence = CASE WHEN status IN ('Deleting', 'Deleted') THEN 'Deleted' ELSE 'Exists' END,
    pending_request_kind = CASE status WHEN 'Creating' THEN 'Create' WHEN 'Attaching' THEN 'Attach'
                                       WHEN 'Detaching' THEN 'Detach' WHEN 'Deleting' THEN 'Delete' END;";

        private const string OldSnapshotColumnsFromTheStatusSql = @"
UPDATE core.volume_snapshots SET
    current_existence = CASE
        WHEN status = 'Deleted' THEN 'Deleted'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN status = 'Available' THEN 'Exists'
        WHEN status = 'Failed' THEN 'NotCreated'
        WHEN status IN ('Creating', 'Deleting') AND observed_state = 'Available' THEN 'Exists'
        WHEN status = 'Creating' THEN 'NotCreated'
        ELSE 'NotFound' END,
    desired_existence = CASE WHEN status IN ('Deleting', 'Deleted') THEN 'Deleted' ELSE 'Exists' END,
    pending_request_kind = CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            foreach (string table in new[] { "volumes", "volume_snapshots" })
            {
                migrationBuilder.AddColumn<string>(name: "status", schema: "core", table: table, type: "text",
                    nullable: false, defaultValue: "Available");
                migrationBuilder.AddColumn<string>(name: "observed_state", schema: "core", table: table, type: "text",
                    nullable: true);
            }
            migrationBuilder.Sql(VolumeStatusFromTheOldColumnsSql);
            migrationBuilder.Sql(SnapshotStatusFromTheOldColumnsSql);
            migrationBuilder.Sql(VolumeObservedStateSql);
            migrationBuilder.Sql(SnapshotObservedStateSql);
            foreach (string table in new[] { "volumes", "volume_snapshots" })
            {
                migrationBuilder.DropColumn(name: "current_existence", schema: "core", table: table);
                migrationBuilder.DropColumn(name: "desired_existence", schema: "core", table: table);
                migrationBuilder.DropColumn(name: "pending_request_kind", schema: "core", table: table);
            }

            migrationBuilder.Sql(ViewWithStorageKindFromStatusSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            foreach (string table in new[] { "volumes", "volume_snapshots" })
            {
                migrationBuilder.AddColumn<string>(name: "current_existence", schema: "core", table: table, type: "text",
                    nullable: false, defaultValue: "NotCreated");
                migrationBuilder.AddColumn<string>(name: "desired_existence", schema: "core", table: table, type: "text",
                    nullable: false, defaultValue: "Exists");
                migrationBuilder.AddColumn<string>(name: "pending_request_kind", schema: "core", table: table, type: "text",
                    nullable: true);
            }
            migrationBuilder.Sql(OldVolumeColumnsFromTheStatusSql);
            migrationBuilder.Sql(OldSnapshotColumnsFromTheStatusSql);
            foreach (string table in new[] { "volumes", "volume_snapshots" })
            {
                migrationBuilder.DropColumn(name: "status", schema: "core", table: table);
                migrationBuilder.DropColumn(name: "observed_state", schema: "core", table: table);
            }

            migrationBuilder.Sql(ViewWithStorageKindColumnSql);
        }
    }
}
