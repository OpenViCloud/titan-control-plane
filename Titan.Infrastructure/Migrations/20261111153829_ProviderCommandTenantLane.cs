using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderCommandTenantLane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "core",
                table: "provider_commands",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lane",
                schema: "core",
                table: "provider_commands",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                computedColumnSql: "CASE WHEN kind IN ('vm.backup', 'volume.backup') THEN 'backup' ELSE 'command' END",
                stored: true);

            // Rows written before the column: every kind of a tenant carries its Incus project `t-<tenant id, 32 hex>`
            // (Tenant.ProjectNameFor); template.sync carries none and stays in the no-tenant lane.
            migrationBuilder.Sql("""
                UPDATE core.provider_commands
                SET tenant_id = substring(payload->>'projectName' from 3)::uuid
                WHERE tenant_id IS NULL AND payload->>'projectName' ~ '^t-[0-9a-f]{32}$';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_provider_commands_kind_leased_no_tenant",
                schema: "core",
                table: "provider_commands",
                column: "kind",
                unique: true,
                filter: "status = 'Leased' AND tenant_id IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_commands_tenant_id_lane_leased",
                schema: "core",
                table: "provider_commands",
                columns: new[] { "tenant_id", "lane" },
                unique: true,
                filter: "status = 'Leased' AND tenant_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_commands_kind_leased_no_tenant",
                schema: "core",
                table: "provider_commands");

            migrationBuilder.DropIndex(
                name: "IX_provider_commands_tenant_id_lane_leased",
                schema: "core",
                table: "provider_commands");

            migrationBuilder.DropColumn(
                name: "lane",
                schema: "core",
                table: "provider_commands");

            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "core",
                table: "provider_commands");
        }
    }
}
