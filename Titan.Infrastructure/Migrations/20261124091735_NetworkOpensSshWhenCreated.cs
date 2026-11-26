using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// Whether this network's create Done also opens its default inbound rules (titan-ops features/network/design.md#default-network):
    /// the networks already there are not the default network, so they get false.
    public partial class NetworkOpensSshWhenCreated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "opens_ssh_when_created",
                schema: "core",
                table: "networks",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "opens_ssh_when_created",
                schema: "core",
                table: "networks");
        }
    }
}
