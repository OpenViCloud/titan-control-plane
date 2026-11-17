using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// A VM's stop timeout (titan-ops features/vm/design.md#vm-stop-timeout): the VMs already there wait 120 s, the
    /// column's default.
    public partial class VmStopTimeout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "stop_timeout_seconds",
                schema: "core",
                table: "vms",
                type: "integer",
                nullable: false,
                defaultValue: 120);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "stop_timeout_seconds",
                schema: "core",
                table: "vms");
        }
    }
}
