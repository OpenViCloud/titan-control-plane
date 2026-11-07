using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// A network and a firewall rule keep one status (titan-ops features/network/design.md#network-lifecycle): status replaces the
    /// desired and existence pairs and the pending kind, current_state becomes observed_state, the Incus status seen
    /// while an operation waits. Written by hand: the scaffold paired the old and new columns by type and renamed
    /// desired_existence to status. The view of waiting requests reads the kind off the status, so it is dropped before the
    /// columns go and made again after.
    /// </summary>
    public partial class NetworkSingleStatus : Migration
    {
        // Old columns to the one status, in the order the old aggregate read them: Deleted absorbs, a waiting operation names
        // its move, then what the reconciler last saw. Without a provider id nothing is on Incus, so Failed (the create gave
        // up before Incus had it; the reconciler lists only rows with an id, and Failed deletes at once). Exists with no word is
        // Unknown, which the reconciler's next cycle replaces.
        private const string NetworkStatusFromTheOldColumnsSql = @"
UPDATE core.networks SET status = CASE
    WHEN current_existence = 'Deleted' THEN 'Deleted'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Delete' THEN 'Deleting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Create' THEN 'Creating'
    WHEN external_id IS NULL THEN 'Failed'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    WHEN current_existence = 'Exists' AND current_state IS NOT NULL THEN current_state
    ELSE 'Unknown'
END;";

        private const string RuleStatusFromTheOldColumnsSql = @"
UPDATE core.firewall_rules SET status = CASE
    WHEN current_existence = 'Deleted' THEN 'Deleted'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Delete' THEN 'Deleting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Create' THEN 'Creating'
    WHEN external_id IS NULL THEN 'Failed'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    WHEN current_existence = 'Exists' AND current_state IS NOT NULL THEN current_state
    ELSE 'Unknown'
END;";

        // The status seen while an operation waits is kept only for a delete that Incus may still show: a network Incus holds
        // keeps its range while it is deleted, and an Exists row with no word is the Incus word Unknown (Network.InstanceStatus).
        private const string NetworkObservedStateSql = @"
UPDATE core.networks SET observed_state =
    CASE WHEN status = 'Deleting' AND current_existence = 'Exists' THEN coalesce(observed_state, 'Unknown') ELSE NULL END;";

        private const string RuleObservedStateSql = @"
UPDATE core.firewall_rules SET observed_state =
    CASE WHEN status = 'Deleting' AND current_existence = 'Exists' THEN observed_state ELSE NULL END;";

        private const string ViewWithNetworkKindFromStatusSql = @"
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


        private const string ViewWithNetworkKindColumnSql = @"
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


        // Down puts the old columns back from the status (observed_state is current_state again by then); a resting row
        // wanted what it has, so desired is Deleted only while a delete waits or after it.
        private const string NetworkOldColumnsFromTheStatusSql = @"
UPDATE core.networks SET
    current_existence = CASE
        WHEN status = 'Deleted' THEN 'Deleted'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN status = 'Deleting' AND current_state IS NULL THEN 'NotFound'
        WHEN status IN ('Creating', 'Failed') THEN 'NotCreated'
        ELSE 'Exists' END,
    desired_existence = CASE WHEN status IN ('Deleting', 'Deleted') THEN 'Deleted' ELSE 'Exists' END,
    pending_request_kind = CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END,
    current_state = CASE
        WHEN status IN ('Pending', 'Created', 'Errored', 'Unavailable', 'Unknown') THEN status
        WHEN status = 'Deleting' THEN current_state
        ELSE NULL END;";

        private const string RuleOldColumnsFromTheStatusSql = @"
UPDATE core.firewall_rules SET
    current_existence = CASE
        WHEN status = 'Deleted' THEN 'Deleted'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN status IN ('Creating', 'Failed') THEN 'NotCreated'
        ELSE 'Exists' END,
    desired_existence = CASE WHEN status IN ('Deleting', 'Deleted') THEN 'Deleted' ELSE 'Exists' END,
    pending_request_kind = CASE status WHEN 'Creating' THEN 'Create' WHEN 'Deleting' THEN 'Delete' END,
    current_state = CASE
        WHEN status IN ('enabled', 'disabled', 'logged') THEN status
        WHEN status = 'Deleting' THEN current_state
        ELSE NULL END;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");
            migrationBuilder.DropIndex(name: "IX_networks_tenant_id_name", schema: "core", table: "networks");

            migrationBuilder.AddColumn<string>(name: "status", schema: "core", table: "networks", type: "text",
                nullable: false, defaultValue: "Unknown");
            migrationBuilder.Sql(NetworkStatusFromTheOldColumnsSql);
            migrationBuilder.RenameColumn(name: "current_state", schema: "core", table: "networks", newName: "observed_state");
            migrationBuilder.Sql(NetworkObservedStateSql);
            migrationBuilder.DropColumn(name: "current_existence", schema: "core", table: "networks");
            migrationBuilder.DropColumn(name: "desired_existence", schema: "core", table: "networks");
            migrationBuilder.DropColumn(name: "pending_request_kind", schema: "core", table: "networks");

            migrationBuilder.AddColumn<string>(name: "status", schema: "core", table: "firewall_rules", type: "text",
                nullable: false, defaultValue: "Unknown");
            migrationBuilder.Sql(RuleStatusFromTheOldColumnsSql);
            migrationBuilder.RenameColumn(name: "current_state", schema: "core", table: "firewall_rules", newName: "observed_state");
            migrationBuilder.Sql(RuleObservedStateSql);
            migrationBuilder.DropColumn(name: "current_existence", schema: "core", table: "firewall_rules");
            migrationBuilder.DropColumn(name: "desired_existence", schema: "core", table: "firewall_rules");
            migrationBuilder.DropColumn(name: "pending_request_kind", schema: "core", table: "firewall_rules");

            migrationBuilder.CreateIndex(name: "IX_networks_tenant_id_name", schema: "core", table: "networks",
                columns: new[] { "tenant_id", "name" }, unique: true, filter: "status <> 'Deleted'");
            migrationBuilder.Sql(ViewWithNetworkKindFromStatusSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");
            migrationBuilder.DropIndex(name: "IX_networks_tenant_id_name", schema: "core", table: "networks");

            migrationBuilder.RenameColumn(name: "observed_state", schema: "core", table: "networks", newName: "current_state");
            migrationBuilder.AddColumn<string>(name: "current_existence", schema: "core", table: "networks", type: "text",
                nullable: false, defaultValue: "NotCreated");
            migrationBuilder.AddColumn<string>(name: "desired_existence", schema: "core", table: "networks", type: "text",
                nullable: false, defaultValue: "Exists");
            migrationBuilder.AddColumn<string>(name: "pending_request_kind", schema: "core", table: "networks", type: "text",
                nullable: true);
            migrationBuilder.Sql(NetworkOldColumnsFromTheStatusSql);
            migrationBuilder.DropColumn(name: "status", schema: "core", table: "networks");

            migrationBuilder.RenameColumn(name: "observed_state", schema: "core", table: "firewall_rules", newName: "current_state");
            migrationBuilder.AddColumn<string>(name: "current_existence", schema: "core", table: "firewall_rules", type: "text",
                nullable: false, defaultValue: "NotCreated");
            migrationBuilder.AddColumn<string>(name: "desired_existence", schema: "core", table: "firewall_rules", type: "text",
                nullable: false, defaultValue: "Exists");
            migrationBuilder.AddColumn<string>(name: "pending_request_kind", schema: "core", table: "firewall_rules", type: "text",
                nullable: true);
            migrationBuilder.Sql(RuleOldColumnsFromTheStatusSql);
            migrationBuilder.DropColumn(name: "status", schema: "core", table: "firewall_rules");

            migrationBuilder.CreateIndex(name: "IX_networks_tenant_id_name", schema: "core", table: "networks",
                columns: new[] { "tenant_id", "name" }, unique: true, filter: "current_existence <> 'Deleted'");
            migrationBuilder.Sql(ViewWithNetworkKindColumnSql);
        }
    }
}
