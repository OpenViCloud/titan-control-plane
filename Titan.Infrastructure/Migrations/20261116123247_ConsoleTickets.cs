using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConsoleTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "console_tickets",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    vm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_console_tickets", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_console_tickets_tenant_id",
                schema: "core",
                table: "console_tickets",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_console_tickets_token_hash",
                schema: "core",
                table: "console_tickets",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "console_tickets",
                schema: "core");
        }
    }
}
