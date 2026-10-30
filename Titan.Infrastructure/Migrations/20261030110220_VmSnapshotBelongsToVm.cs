using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VmSnapshotBelongsToVm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_vm_snapshots_vms_vm_id",
                schema: "core",
                table: "vm_snapshots",
                column: "vm_id",
                principalSchema: "core",
                principalTable: "vms",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_vm_snapshots_vms_vm_id",
                schema: "core",
                table: "vm_snapshots");
        }
    }
}
