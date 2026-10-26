using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "tenant_id",
                schema: "core",
                table: "backups",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "tenant_id",
                schema: "core",
                table: "backups");
        }
    }
}
