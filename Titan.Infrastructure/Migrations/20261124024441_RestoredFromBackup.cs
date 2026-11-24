using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// Marks the VMs and volumes an admin restores from a backup (titan-ops features/backup/design.md#restore-as-new-resource),
    /// which count toward no quota and are never billed: every VM and volume already there is not one, the columns' default.
    public partial class RestoredFromBackup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "restored_from_backup",
                schema: "core",
                table: "volumes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "restored_from_backup",
                schema: "core",
                table: "vms",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "restored_from_backup",
                schema: "core",
                table: "volumes");

            migrationBuilder.DropColumn(
                name: "restored_from_backup",
                schema: "core",
                table: "vms");
        }
    }
}
