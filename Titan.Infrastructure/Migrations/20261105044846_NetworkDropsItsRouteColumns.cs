using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A network's public IPv6 /64 is held in core.cluster_ipv6_routes (ClusterIpv6Route); the three columns that held
    /// it before, and their unique index, go now that no released image writes them. An image from before this migration
    /// still maps them: rolling one back needs this migration's Down first. Down brings the columns back empty: their
    /// values are not kept, and no image since ClusterIpv6Route reads them.
    public partial class NetworkDropsItsRouteColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_networks_cluster_id_public_ipv6_index",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "public_ipv6_index",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "public_ipv6_released_at",
                schema: "core",
                table: "networks");

            migrationBuilder.DropColumn(
                name: "public_ipv6_router_address",
                schema: "core",
                table: "networks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "public_ipv6_index",
                schema: "core",
                table: "networks",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "public_ipv6_released_at",
                schema: "core",
                table: "networks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_ipv6_router_address",
                schema: "core",
                table: "networks",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_networks_cluster_id_public_ipv6_index",
                schema: "core",
                table: "networks",
                columns: new[] { "cluster_id", "public_ipv6_index" },
                unique: true,
                filter: "public_ipv6_index IS NOT NULL AND current_existence <> 'Deleted'");
        }
    }
}
