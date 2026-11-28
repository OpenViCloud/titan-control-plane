using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <summary>
    /// A function version's configuration (titan-ops features/ovc-function/design.md#function-config): core.function_versions
    /// gains env (jsonb object, '{}' for every version uploaded before), secret_refs (jsonb array, '[]') and schedule (null);
    /// core.function_secrets and core.function_secret_versions hold a tenant's secrets, ciphertext only, one name per tenant
    /// and one number per secret (titan-ops features/ovc-function/design.md#function-secrets). The python3.15 and nodejs26
    /// runtimes move to the images titan-function-runtimes published with the configuration.
    /// </summary>
    public partial class FunctionConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "env",
                schema: "core",
                table: "function_versions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "schedule",
                schema: "core",
                table: "function_versions",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "secret_refs",
                schema: "core",
                table: "function_versions",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateTable(
                name: "function_secrets",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_function_secrets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "function_secret_versions",
                schema: "core",
                columns: table => new
                {
                    secret_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    key_id = table.Column<string>(type: "text", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    value_bytes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_function_secret_versions", x => new { x.secret_id, x.number });
                    table.ForeignKey(
                        name: "FK_function_secret_versions_function_secrets_secret_id",
                        column: x => x.secret_id,
                        principalSchema: "core",
                        principalTable: "function_secrets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                schema: "core",
                table: "function_runtimes",
                keyColumn: "id",
                keyValue: new Guid("f0de0000-0000-4000-8000-000000000001"),
                column: "image",
                value: "ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:5479dbe0d05ec82ba2ba0e6995a95619b231019639a7bf9aeb393a19d61c5a0b");

            migrationBuilder.UpdateData(
                schema: "core",
                table: "function_runtimes",
                keyColumn: "id",
                keyValue: new Guid("f0de0000-0000-4000-8000-000000000002"),
                column: "image",
                value: "ghcr.io/openvicloud/titan-function-runtimes/nodejs26@sha256:9f06d060020e71de5e31fa6a8bfcd6b85cca5491282df2b28abeec4b1e386c3c");

            migrationBuilder.CreateIndex(
                name: "IX_function_secrets_tenant_id_name",
                schema: "core",
                table: "function_secrets",
                columns: new[] { "tenant_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "function_secret_versions",
                schema: "core");

            migrationBuilder.DropTable(
                name: "function_secrets",
                schema: "core");

            migrationBuilder.DropColumn(
                name: "env",
                schema: "core",
                table: "function_versions");

            migrationBuilder.DropColumn(
                name: "schedule",
                schema: "core",
                table: "function_versions");

            migrationBuilder.DropColumn(
                name: "secret_refs",
                schema: "core",
                table: "function_versions");

            migrationBuilder.UpdateData(
                schema: "core",
                table: "function_runtimes",
                keyColumn: "id",
                keyValue: new Guid("f0de0000-0000-4000-8000-000000000001"),
                column: "image",
                value: "ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:c2c3c3e141ea062fa0cb4c11119c8bf45184d98a22f7f384456fd4082883b8d1");

            migrationBuilder.UpdateData(
                schema: "core",
                table: "function_runtimes",
                keyColumn: "id",
                keyValue: new Guid("f0de0000-0000-4000-8000-000000000002"),
                column: "image",
                value: "ghcr.io/openvicloud/titan-function-runtimes/nodejs26@sha256:3f12d5472d95dfb85ad20ba7d970eccfdbf25d4073a9ee46b71e7434d8eb1fcd");
        }
    }
}
