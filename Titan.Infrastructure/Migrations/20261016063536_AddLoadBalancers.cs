using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadBalancers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The scaffold wrote 0, and 0 is "none allowed" (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none):
            // every existing tenant would be refused its first load balancer. Backfill with the default cap, as
            // AddSshKeys did (TenantQuota.DefaultMaxLoadBalancers).
            migrationBuilder.AddColumn<int>(
                name: "max_load_balancers",
                schema: "core",
                table: "tenants",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "public_ipv6_released_at",
                schema: "core",
                table: "networks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "load_balancers",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    network_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_network_id = table.Column<string>(type: "text", nullable: false),
                    network_subnet = table.Column<string>(type: "text", nullable: false),
                    zone_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    vip_index = table.Column<int>(type: "integer", nullable: false),
                    vip = table.Column<string>(type: "text", nullable: false),
                    external_id = table.Column<string>(type: "text", nullable: true),
                    current_existence = table.Column<string>(type: "text", nullable: false),
                    desired_existence = table.Column<string>(type: "text", nullable: false),
                    pending_intent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_failure_reason = table.Column<string>(type: "text", nullable: true),
                    request_failure_detail = table.Column<string>(type: "text", nullable: true),
                    request_failed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_load_balancers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "load_balancer_backends",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    load_balancer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_vm_id = table.Column<string>(type: "text", nullable: false),
                    is_desired = table.Column<bool>(type: "boolean", nullable: false),
                    is_applied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_load_balancer_backends", x => x.id);
                    table.ForeignKey(
                        name: "FK_load_balancer_backends_load_balancers_load_balancer_id",
                        column: x => x.load_balancer_id,
                        principalSchema: "core",
                        principalTable: "load_balancers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "load_balancer_listeners",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    load_balancer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protocol = table.Column<string>(type: "text", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: false),
                    target_port = table.Column<int>(type: "integer", nullable: false),
                    is_desired = table.Column<bool>(type: "boolean", nullable: false),
                    is_applied = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_load_balancer_listeners", x => x.id);
                    table.ForeignKey(
                        name: "FK_load_balancer_listeners_load_balancers_load_balancer_id",
                        column: x => x.load_balancer_id,
                        principalSchema: "core",
                        principalTable: "load_balancers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_load_balancer_backends_load_balancer_id",
                schema: "core",
                table: "load_balancer_backends",
                column: "load_balancer_id");

            migrationBuilder.CreateIndex(
                name: "IX_load_balancer_backends_vm_id",
                schema: "core",
                table: "load_balancer_backends",
                column: "vm_id");

            migrationBuilder.CreateIndex(
                name: "IX_load_balancer_listeners_load_balancer_id",
                schema: "core",
                table: "load_balancer_listeners",
                column: "load_balancer_id");

            migrationBuilder.CreateIndex(
                name: "ix_load_balancers_network_id_vip_index",
                schema: "core",
                table: "load_balancers",
                columns: new[] { "network_id", "vip_index" },
                unique: true,
                filter: "current_existence <> 'Deleted'");

            migrationBuilder.CreateIndex(
                name: "IX_load_balancers_tenant_id_name",
                schema: "core",
                table: "load_balancers",
                columns: new[] { "tenant_id", "name" },
                unique: true,
                filter: "current_existence <> 'Deleted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "load_balancer_backends",
                schema: "core");

            migrationBuilder.DropTable(
                name: "load_balancer_listeners",
                schema: "core");

            migrationBuilder.DropTable(
                name: "load_balancers",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "max_load_balancers",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "public_ipv6_released_at",
                schema: "core",
                table: "networks");
        }
    }
}
