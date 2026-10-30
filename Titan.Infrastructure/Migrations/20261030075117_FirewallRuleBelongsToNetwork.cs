using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A firewall rule is its network's child entity (titan-ops architecture/code-structure.md#child-entity): the table
    /// keeps its name and columns and gains the key that says so. Restrict, not cascade: no row of either table is
    /// ever deleted (titan-ops features/network/design.md#firewall-rule-soft-delete).
    public partial class FirewallRuleBelongsToNetwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_firewall_rules_networks_network_id",
                schema: "core",
                table: "firewall_rules",
                column: "network_id",
                principalSchema: "core",
                principalTable: "networks",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_firewall_rules_networks_network_id",
                schema: "core",
                table: "firewall_rules");
        }
    }
}
