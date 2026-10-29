using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSshKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "max_ssh_keys",
                schema: "core",
                table: "tenants",
                type: "integer",
                nullable: false,
                // Hand-edited from 0: a cap of 0 means "no key allowed" (titan-ops architecture/tenancy-and-admin.md#quota-zero-means-none),
                // so existing tenants get the default cap, as every earlier cap's migration did.
                defaultValue: 20);

            migrationBuilder.CreateTable(
                name: "ssh_keys",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    public_key = table.Column<string>(type: "text", nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ssh_keys", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ssh_keys_tenant_id_fingerprint",
                schema: "core",
                table: "ssh_keys",
                columns: new[] { "tenant_id", "fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ssh_keys_tenant_id_name",
                schema: "core",
                table: "ssh_keys",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ssh_keys",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "max_ssh_keys",
                schema: "core",
                table: "tenants");
        }
    }
}
