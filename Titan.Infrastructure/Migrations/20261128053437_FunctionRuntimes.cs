using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// The runtime catalog of OVC Function (titan-ops features/ovc-function/design.md#runtimes): function_runtimes, global
    /// and seeded with the three runtimes, and function_runtime_clusters, one sync state per runtime per function cluster,
    /// with the request columns of vm_template_clusters and no provider id. A waiting sync is a stuck-request candidate like a template's
    /// (titan-ops architecture/resource-lifecycle.md#lost-and-stale): core.pending_requests is dropped and recreated with
    /// its branch, copied from the last migration that defined the view (VmKeyAdditions) plus this one UNION ALL. The
    /// branch is keyed as ProviderCommandQueue.FunctionRuntimeSyncKey: function-runtime-sync-{id}-{cluster}-{request}.
    /// </summary>
    public partial class FunctionRuntimes : Migration
    {
        private const string ViewWithoutFunctionRuntimesSql = @"
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

        private const string ViewWithFunctionRuntimesSql = @"
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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.CreateTable(
                name: "function_runtimes",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    image = table.Column<string>(type: "text", nullable: false),
                    architecture = table.Column<string>(type: "text", nullable: false),
                    is_listed = table.Column<bool>(type: "boolean", nullable: false),
                    support_ends_on = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_function_runtimes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "function_runtime_clusters",
                schema: "core",
                columns: table => new
                {
                    runtime_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cluster_id = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    pending_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    pending_request_asked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    request_failure_reason = table.Column<string>(type: "text", nullable: true),
                    request_failure_detail = table.Column<string>(type: "text", nullable: true),
                    request_failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_state = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_function_runtime_clusters", x => new { x.runtime_id, x.cluster_id });
                    table.ForeignKey(
                        name: "FK_function_runtime_clusters_function_runtimes_runtime_id",
                        column: x => x.runtime_id,
                        principalSchema: "core",
                        principalTable: "function_runtimes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "core",
                table: "function_runtimes",
                columns: new[] { "id", "architecture", "created_at", "image", "is_listed", "name", "support_ends_on" },
                values: new object[,]
                {
                    { new Guid("f0de0000-0000-4000-8000-000000000001"), "amd64", new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:c2c3c3e141ea062fa0cb4c11119c8bf45184d98a22f7f384456fd4082883b8d1", true, "python3.15", new DateOnly(2031, 10, 31) },
                    { new Guid("f0de0000-0000-4000-8000-000000000002"), "amd64", new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "ghcr.io/openvicloud/titan-function-runtimes/nodejs26@sha256:3f12d5472d95dfb85ad20ba7d970eccfdbf25d4073a9ee46b71e7434d8eb1fcd", true, "nodejs26", new DateOnly(2029, 4, 30) },
                    { new Guid("f0de0000-0000-4000-8000-000000000003"), "amd64", new DateTimeOffset(new DateTime(2026, 11, 28, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "ghcr.io/openvicloud/titan-function-runtimes/go@sha256:f253fc8f929cefbfdf899cdf8804374c0eb78e23d650f552840ea9e334d116dd", true, "go", null }
                });

            migrationBuilder.CreateIndex(
                name: "UX_function_runtimes_name",
                schema: "core",
                table: "function_runtimes",
                column: "name",
                unique: true);

            migrationBuilder.Sql(ViewWithFunctionRuntimesSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW core.pending_requests;");

            migrationBuilder.DropTable(
                name: "function_runtime_clusters",
                schema: "core");

            migrationBuilder.DropTable(
                name: "function_runtimes",
                schema: "core");

            migrationBuilder.Sql(ViewWithoutFunctionRuntimesSql);
        }
    }
}
