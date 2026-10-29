using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkPublicIpv6 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Kept for the pods still running the old model during the rollout; a default lets the new model insert
            // without it. A later migration drops it (titan-ops features/vm/design.md#vm-design-debts).
            migrationBuilder.AlterColumn<bool>(
                name: "enable_public_ipv6",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddColumn<int>(
                name: "public_ipv6_index",
                schema: "core",
                table: "networks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_ipv6_router_address",
                schema: "core",
                table: "networks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_ipv6_subnet",
                schema: "core",
                table: "networks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ipv6_block",
                schema: "core",
                table: "clusters",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_networks_zone_id_public_ipv6_index",
                schema: "core",
                table: "networks",
                columns: new[] { "zone_id", "public_ipv6_index" },
                unique: true,
                filter: "public_ipv6_index IS NOT NULL AND current_existence <> 'Deleted'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_networks_zone_id_public_ipv6_index",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "public_ipv6_index",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "public_ipv6_router_address",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "public_ipv6_subnet",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "ipv6_block",
                schema: "core",
                table: "clusters");

            migrationBuilder.AlterColumn<bool>(
                name: "enable_public_ipv6",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);
        }
    }
}
