using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ProviderCommandResource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rows written before this migration keep resource_id null and so hold no resource: no backfill, as a
            // command of an old row only overlaps a backup of its resource for the one lease in flight at the deploy.
            migrationBuilder.AddColumn<Guid>(
                name: "resource_id",
                schema: "core",
                table: "provider_commands",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_commands_resource_id_leased",
                schema: "core",
                table: "provider_commands",
                column: "resource_id",
                unique: true,
                filter: "status = 'Leased' AND resource_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_commands_resource_id_leased",
                schema: "core",
                table: "provider_commands");

            migrationBuilder.DropColumn(
                name: "resource_id",
                schema: "core",
                table: "provider_commands");
        }
    }
}
