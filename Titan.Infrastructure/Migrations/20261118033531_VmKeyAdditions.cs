using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// A new child table, vm_key_additions (titan-ops features/vm/design.md#ssh-key), and its branch in
    /// core.pending_requests: an addition waiting on its operation (status Adding) is a stuck-request candidate like
    /// every other lifecycle table's waiting row (titan-ops architecture/resource-lifecycle.md#lost-and-stale). The view
    /// is dropped and recreated with the branch added, copied from the previous migration that defined it
    /// (LoadBalancerSingleStatus) plus this one UNION ALL.
    /// </summary>
    public partial class VmKeyAdditions : Migration
    {
        private const string ViewWithoutKeyAdditionsSql = @"
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

        private const string ViewWithKeyAdditionsSql = @"
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
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'vm-key-addition', id, tenant_id, pending_request_id,
       CASE status WHEN 'Adding' THEN 'Create' END, pending_request_asked_at, NULL::text
FROM core.vm_key_additions
WHERE pending_request_id IS NOT NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");
            migrationBuilder.CreateTable(
                name: "vm_key_additions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    vm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    pending_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_request_asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    request_failure_reason = table.Column<string>(type: "text", nullable: true),
                    request_failure_detail = table.Column<string>(type: "text", nullable: true),
                    request_failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    restart_needed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ssh_key_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_key_additions", x => x.id);
                    table.ForeignKey(
                        name: "FK_vm_key_additions_vms_vm_id",
                        column: x => x.vm_id,
                        principalSchema: "core",
                        principalTable: "vms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_vm_key_additions_tenant_id",
                schema: "core",
                table: "vm_key_additions",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_vm_key_additions_vm_id",
                schema: "core",
                table: "vm_key_additions",
                column: "vm_id");

            migrationBuilder.Sql(ViewWithKeyAdditionsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.DropTable(
                name: "vm_key_additions",
                schema: "core");

            migrationBuilder.Sql(ViewWithoutKeyAdditionsSql);
        }
    }
}
