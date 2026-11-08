using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// A load balancer keeps one status (titan-ops features/load-balancer/design.md#lb-lifecycle): status replaces the
    /// existence pair and the pending kind, and observed_state says whether Incus was last seen holding the load balancer
    /// while an operation waits. Written by hand: the scaffold paired the old and new columns by type and renamed
    /// desired_existence to status and pending_request_kind to observed_state. Both unique indexes filtered on
    /// current_existence, so they are dropped before the column goes and made again, with the same names and columns,
    /// filtering on status (ErrorStatus reads the VIP index name for 409 LOAD_BALANCER_VIP_CONFLICT). The view of waiting
    /// requests reads the kind off the status, so it is dropped before the columns go and made again after.
    /// </summary>
    public partial class LoadBalancerSingleStatus : Migration
    {
        // Old columns to the one status, in the order the old row read them: gone, then the waiting operation, then no
        // VIP (a create that gave up before Incus had it), then what the reconciler last saw.
        private const string StatusFromTheOldColumnsSql = @"
UPDATE core.load_balancers SET status = CASE
    WHEN current_existence = 'Deleted' THEN 'Deleted'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Delete' THEN 'Deleting'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Update' THEN 'Updating'
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Create' THEN 'Creating'
    WHEN external_id IS NULL THEN 'Failed'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    ELSE 'Available'
END;";

        // While an operation waits, whether Incus held the load balancer is kept: the quota counts an update only while it
        // does, and a give-up of an update or a delete returns to what was seen (LoadBalancer.InstanceStatus).
        private const string ObservedStateSql = @"
UPDATE core.load_balancers SET observed_state =
    CASE WHEN status IN ('Creating', 'Updating', 'Deleting') AND current_existence = 'Exists' THEN 'Available' ELSE NULL END;";

        private const string ViewWithLoadBalancerKindColumnSql = @"
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
SELECT 'template', template_id, NULL::uuid, pending_request_id,
       CASE status WHEN 'Syncing' THEN 'Sync' END, pending_request_asked_at, cluster_id
FROM core.vm_template_clusters
WHERE pending_request_id IS NOT NULL;";

        private const string ViewWithLoadBalancerKindFromStatusSql = @"
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
SELECT 'load-balancer', id, tenant_id, pending_request_id,
       CASE status WHEN 'Creating' THEN 'Create' WHEN 'Updating' THEN 'Update' WHEN 'Deleting' THEN 'Delete' END,
       pending_request_asked_at, NULL::text
FROM core.load_balancers
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'template', template_id, NULL::uuid, pending_request_id,
       CASE status WHEN 'Syncing' THEN 'Sync' END, pending_request_asked_at, cluster_id
FROM core.vm_template_clusters
WHERE pending_request_id IS NOT NULL;";

        // Down puts the old columns back from the status and observed_state. A waiting row exists when the operation last
        // saw Incus holding it; a create that has not been seen is NotCreated, an update or a delete that has not is NotFound.
        private const string OldColumnsFromTheStatusSql = @"
UPDATE core.load_balancers SET
    current_existence = CASE
        WHEN status = 'Deleted' THEN 'Deleted'
        WHEN status = 'Available' THEN 'Exists'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN status = 'Failed' THEN 'NotCreated'
        WHEN observed_state = 'Available' THEN 'Exists'
        WHEN status = 'Creating' THEN 'NotCreated'
        ELSE 'NotFound' END,
    desired_existence = CASE WHEN status IN ('Deleting', 'Deleted') THEN 'Deleted' ELSE 'Exists' END,
    pending_request_kind = CASE status
        WHEN 'Creating' THEN 'Create' WHEN 'Updating' THEN 'Update' WHEN 'Deleting' THEN 'Delete' END;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");
            migrationBuilder.DropIndex(name: "ix_load_balancers_network_id_vip_index", schema: "core", table: "load_balancers");
            migrationBuilder.DropIndex(name: "IX_load_balancers_tenant_id_name", schema: "core", table: "load_balancers");

            migrationBuilder.AddColumn<string>(name: "status", schema: "core", table: "load_balancers", type: "text",
                nullable: false, defaultValue: "Available");
            migrationBuilder.AddColumn<string>(name: "observed_state", schema: "core", table: "load_balancers", type: "text",
                nullable: true);
            migrationBuilder.Sql(StatusFromTheOldColumnsSql);
            migrationBuilder.Sql(ObservedStateSql);
            migrationBuilder.DropColumn(name: "current_existence", schema: "core", table: "load_balancers");
            migrationBuilder.DropColumn(name: "desired_existence", schema: "core", table: "load_balancers");
            migrationBuilder.DropColumn(name: "pending_request_kind", schema: "core", table: "load_balancers");

            migrationBuilder.CreateIndex(name: "ix_load_balancers_network_id_vip_index", schema: "core", table: "load_balancers",
                columns: new[] { "network_id", "vip_index" }, unique: true, filter: "status <> 'Deleted'");
            migrationBuilder.CreateIndex(name: "IX_load_balancers_tenant_id_name", schema: "core", table: "load_balancers",
                columns: new[] { "tenant_id", "name" }, unique: true, filter: "status <> 'Deleted'");
            migrationBuilder.Sql(ViewWithLoadBalancerKindFromStatusSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");
            migrationBuilder.DropIndex(name: "ix_load_balancers_network_id_vip_index", schema: "core", table: "load_balancers");
            migrationBuilder.DropIndex(name: "IX_load_balancers_tenant_id_name", schema: "core", table: "load_balancers");

            migrationBuilder.AddColumn<string>(name: "current_existence", schema: "core", table: "load_balancers", type: "text",
                nullable: false, defaultValue: "NotCreated");
            migrationBuilder.AddColumn<string>(name: "desired_existence", schema: "core", table: "load_balancers", type: "text",
                nullable: false, defaultValue: "Exists");
            migrationBuilder.AddColumn<string>(name: "pending_request_kind", schema: "core", table: "load_balancers", type: "text",
                nullable: true);
            migrationBuilder.Sql(OldColumnsFromTheStatusSql);
            migrationBuilder.DropColumn(name: "status", schema: "core", table: "load_balancers");
            migrationBuilder.DropColumn(name: "observed_state", schema: "core", table: "load_balancers");

            migrationBuilder.CreateIndex(name: "ix_load_balancers_network_id_vip_index", schema: "core", table: "load_balancers",
                columns: new[] { "network_id", "vip_index" }, unique: true, filter: "current_existence <> 'Deleted'");
            migrationBuilder.CreateIndex(name: "IX_load_balancers_tenant_id_name", schema: "core", table: "load_balancers",
                columns: new[] { "tenant_id", "name" }, unique: true, filter: "current_existence <> 'Deleted'");
            migrationBuilder.Sql(ViewWithLoadBalancerKindColumnSql);
        }
    }
}
