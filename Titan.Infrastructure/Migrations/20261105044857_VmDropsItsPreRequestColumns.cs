using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A VM's waiting request is its own three columns (titan-ops architecture/resource-lifecycle.md#shape); the two
    /// columns it was once derived from, kept with DEFAULT false so an image still reading them could run, go now that
    /// no released image reads them. Written by hand: the model has not mapped them since VmPendingRequest, so the
    /// scaffold sees nothing to drop. Down puts them back as VmPendingRequest left them, every row false.
    public partial class VmDropsItsPreRequestColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "create_finished",
                schema: "core",
                table: "vms");

            migrationBuilder.DropColumn(
                name: "has_been_running",
                schema: "core",
                table: "vms");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "has_been_running",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "create_finished",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
