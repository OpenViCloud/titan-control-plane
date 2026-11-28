using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// OVC Function's functions (titan-ops features/ovc-function/design.md#function-lifecycle): core.functions, with the
    /// request columns of the other provisioned tables, and core.function_versions, one row per uploaded version. A
    /// waiting deploy or delete is a stuck-request candidate (titan-ops architecture/resource-lifecycle.md#lost-and-stale):
    /// core.pending_requests is dropped and recreated with its branch, copied from the last migration that defined the
    /// view (FunctionRuntimes) plus this one UNION ALL, keyed as ProviderCommandQueue.FunctionDeployKey and
    /// FunctionDeleteKey. The provider command lane gains a third value, function, so a tenant's function command and its
    /// Incus commands do not hold each other back (titan-ops features/provider/design.md#tenant-lane).
    /// </summary>
    public partial class Functions : Migration
    {
        private const string ViewWithoutFunctionsSql = @"
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
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'function-runtime', runtime_id, NULL::uuid, pending_request_id,
       CASE status WHEN 'Syncing' THEN 'Sync' END, pending_request_asked_at, cluster_id
FROM core.function_runtime_clusters
WHERE pending_request_id IS NOT NULL;";

        private const string ViewWithFunctionsSql = @"
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
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'function-runtime', runtime_id, NULL::uuid, pending_request_id,
       CASE status WHEN 'Syncing' THEN 'Sync' END, pending_request_asked_at, cluster_id
FROM core.function_runtime_clusters
WHERE pending_request_id IS NOT NULL
UNION ALL
SELECT 'function', id, tenant_id, pending_request_id,
       CASE status WHEN 'Deploying' THEN 'Deploy' WHEN 'Deleting' THEN 'Delete' END, pending_request_asked_at, NULL::text
FROM core.functions
WHERE pending_request_id IS NOT NULL;";
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The lane is a stored generated column, which Postgres cannot alter in place: drop it with the index that reads
            // it, and add both again with the function lane. Dropping the column alone would drop the index silently.
            migrationBuilder.DropIndex(
                name: "IX_provider_commands_tenant_id_lane_leased",
                schema: "core",
                table: "provider_commands");
            migrationBuilder.DropColumn(
                name: "lane",
                schema: "core",
                table: "provider_commands");
            migrationBuilder.AddColumn<string>(
                name: "lane",
                schema: "core",
                table: "provider_commands",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                computedColumnSql: "CASE WHEN kind IN ('vm.backup', 'volume.backup') THEN 'backup' WHEN kind IN ('function.deploy', 'function.delete') THEN 'function' ELSE 'command' END",
                stored: true);
            migrationBuilder.CreateIndex(
                name: "IX_provider_commands_tenant_id_lane_leased",
                schema: "core",
                table: "provider_commands",
                columns: new[] { "tenant_id", "lane" },
                unique: true,
                filter: "status = 'Leased' AND tenant_id IS NOT NULL");

            migrationBuilder.Sql("DROP VIEW core.pending_requests;");


            migrationBuilder.CreateTable(
                name: "functions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cluster_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    pending_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_request_asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    request_failure_reason = table.Column<string>(type: "text", nullable: true),
                    request_failure_detail = table.Column<string>(type: "text", nullable: true),
                    request_failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_state = table.Column<string>(type: "text", nullable: true),
                    running_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    deploying_version_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_functions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "function_versions",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    function_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    runtime_id = table.Column<Guid>(type: "uuid", nullable: false),
                    runtime_name = table.Column<string>(type: "text", nullable: false),
                    runtime_image = table.Column<string>(type: "text", nullable: false),
                    handler = table.Column<string>(type: "text", nullable: true),
                    memory_mib = table.Column<int>(type: "integer", nullable: false),
                    timeout_seconds = table.Column<int>(type: "integer", nullable: false),
                    code_key = table.Column<string>(type: "text", nullable: false),
                    code_sha256 = table.Column<string>(type: "text", nullable: false),
                    code_bytes = table.Column<long>(type: "bigint", nullable: false),
                    unpacked_bytes = table.Column<long>(type: "bigint", nullable: false),
                    file_count = table.Column<int>(type: "integer", nullable: false),
                    image_digest = table.Column<string>(type: "text", nullable: false),
                    image = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    discarded_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_function_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_function_versions_functions_function_id",
                        column: x => x.function_id,
                        principalSchema: "core",
                        principalTable: "functions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_function_versions_function_id_number",
                schema: "core",
                table: "function_versions",
                columns: new[] { "function_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_functions_tenant_id",
                schema: "core",
                table: "functions",
                column: "tenant_id");

            migrationBuilder.Sql(ViewWithFunctionsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.DropTable(
                name: "function_versions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "functions",
                schema: "core");

            // As in Up, without the function lane.
            migrationBuilder.DropIndex(
                name: "IX_provider_commands_tenant_id_lane_leased",
                schema: "core",
                table: "provider_commands");
            migrationBuilder.DropColumn(
                name: "lane",
                schema: "core",
                table: "provider_commands");
            migrationBuilder.AddColumn<string>(
                name: "lane",
                schema: "core",
                table: "provider_commands",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                computedColumnSql: "CASE WHEN kind IN ('vm.backup', 'volume.backup') THEN 'backup' ELSE 'command' END",
                stored: true);
            migrationBuilder.CreateIndex(
                name: "IX_provider_commands_tenant_id_lane_leased",
                schema: "core",
                table: "provider_commands",
                columns: new[] { "tenant_id", "lane" },
                unique: true,
                filter: "status = 'Leased' AND tenant_id IS NOT NULL");

            migrationBuilder.Sql(ViewWithoutFunctionsSql);
        }
    }
}
