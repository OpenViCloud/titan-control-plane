using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// A template in a cluster keeps one status (titan-ops features/catalog/design.md#template-state-is-existence): status
    /// replaces the existence pair and the pending kind, and observed_state says whether the cluster was last seen holding
    /// the alias while a sync waits. Written by hand: the scaffold paired the old and new columns by type and renamed
    /// desired_existence to status and pending_request_kind to observed_state. The view of waiting requests reads the kind
    /// off the status, so it is dropped before the columns go and made again after.
    /// </summary>
    public partial class VmTemplateClusterSingleStatus : Migration
    {
        // Old columns to the one status, in the order the old row read them: a waiting sync, then no alias (NotFound when
        // the reconciler's observation made the row and no sync gave up, Failed otherwise), then what the reconciler last saw.
        private const string StatusFromTheOldColumnsSql = @"
UPDATE core.vm_template_clusters SET status = CASE
    WHEN pending_request_id IS NOT NULL AND pending_request_kind = 'Sync' THEN 'Syncing'
    WHEN external_template_id IS NULL AND current_existence = 'NotFound' AND request_failure_reason IS NULL THEN 'NotFound'
    WHEN external_template_id IS NULL THEN 'Failed'
    WHEN current_existence = 'NotFound' THEN 'NotFound'
    ELSE 'Available'
END;";

        // While a sync waits, whether the cluster held the alias is kept: customers see the template while it does
        // (VmTemplateCluster.InstanceStatus).
        private const string ObservedStateSql = @"
UPDATE core.vm_template_clusters SET observed_state =
    CASE WHEN status = 'Syncing' AND current_existence = 'Exists' THEN 'Available' ELSE NULL END;";

        private const string ViewWithTemplateKindColumnSql = @"
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

        private const string ViewWithTemplateKindFromStatusSql = @"
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

        // Down puts the old columns back from the status and observed_state. Sync is add-only, so desired is always Exists;
        // a waiting row exists when the sync last saw the alias, and holds a provider id of the alias only once it had one.
        private const string OldColumnsFromTheStatusSql = @"
UPDATE core.vm_template_clusters SET
    current_existence = CASE
        WHEN status = 'Available' THEN 'Exists'
        WHEN status = 'NotFound' THEN 'NotFound'
        WHEN status = 'Failed' THEN 'NotCreated'
        WHEN observed_state = 'Available' THEN 'Exists'
        WHEN external_template_id IS NULL THEN 'NotCreated'
        ELSE 'NotFound' END,
    desired_existence = 'Exists',
    pending_request_kind = CASE status WHEN 'Syncing' THEN 'Sync' END;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.AddColumn<string>(name: "status", schema: "core", table: "vm_template_clusters", type: "text",
                nullable: false, defaultValue: "Available");
            migrationBuilder.AddColumn<string>(name: "observed_state", schema: "core", table: "vm_template_clusters", type: "text",
                nullable: true);
            migrationBuilder.Sql(StatusFromTheOldColumnsSql);
            migrationBuilder.Sql(ObservedStateSql);
            migrationBuilder.DropColumn(name: "current_existence", schema: "core", table: "vm_template_clusters");
            migrationBuilder.DropColumn(name: "desired_existence", schema: "core", table: "vm_template_clusters");
            migrationBuilder.DropColumn(name: "pending_request_kind", schema: "core", table: "vm_template_clusters");

            migrationBuilder.Sql(ViewWithTemplateKindFromStatusSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.AddColumn<string>(name: "current_existence", schema: "core", table: "vm_template_clusters", type: "text",
                nullable: false, defaultValue: "NotCreated");
            migrationBuilder.AddColumn<string>(name: "desired_existence", schema: "core", table: "vm_template_clusters", type: "text",
                nullable: false, defaultValue: "Exists");
            migrationBuilder.AddColumn<string>(name: "pending_request_kind", schema: "core", table: "vm_template_clusters", type: "text",
                nullable: true);
            migrationBuilder.Sql(OldColumnsFromTheStatusSql);
            migrationBuilder.DropColumn(name: "status", schema: "core", table: "vm_template_clusters");
            migrationBuilder.DropColumn(name: "observed_state", schema: "core", table: "vm_template_clusters");

            migrationBuilder.Sql(ViewWithTemplateKindColumnSql);
        }
    }
}
