using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// Drops ten columns that the control plane reads in no code and no view. Their values are not kept: Down brings the columns back with a
    /// default (finished_at as null), and the seed values of the catalog rows.
    public partial class AggregateFieldsClean : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "created_at",
                schema: "core",
                table: "wallets");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "core",
                table: "wallets");

            migrationBuilder.DropColumn(
                name: "is_customized",
                schema: "core",
                table: "volume_offerings");

            migrationBuilder.DropColumn(
                name: "size_gb",
                schema: "core",
                table: "volume_offerings");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "core",
                table: "vm_templates");

            migrationBuilder.DropColumn(
                name: "updated_at",
                schema: "core",
                table: "vm_template_clusters");

            migrationBuilder.DropColumn(
                name: "cpu_units",
                schema: "core",
                table: "vm_offerings");

            migrationBuilder.DropColumn(
                name: "email",
                schema: "core",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "finished_at",
                schema: "core",
                table: "provider_commands");

            migrationBuilder.DropColumn(
                name: "observed_at",
                schema: "core",
                table: "backups");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "created_at",
                schema: "core",
                table: "wallets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "core",
                table: "wallets",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<bool>(
                name: "is_customized",
                schema: "core",
                table: "volume_offerings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "size_gb",
                schema: "core",
                table: "volume_offerings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "core",
                table: "vm_templates",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "updated_at",
                schema: "core",
                table: "vm_template_clusters",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<decimal>(
                name: "cpu_units",
                schema: "core",
                table: "vm_offerings",
                type: "numeric(5,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "email",
                schema: "core",
                table: "tenants",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "finished_at",
                schema: "core",
                table: "provider_commands",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "observed_at",
                schema: "core",
                table: "backups",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "l",
                column: "cpu_units",
                value: 2.0m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "m",
                column: "cpu_units",
                value: 1.0m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "s",
                column: "cpu_units",
                value: 0.5m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "xl",
                column: "cpu_units",
                value: 4.0m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_templates",
                keyColumn: "id",
                keyValue: new Guid("0a5e0000-0000-4000-8000-000000000002"),
                column: "updated_at",
                value: new DateTimeOffset(new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_templates",
                keyColumn: "id",
                keyValue: new Guid("0a5e0000-0000-4000-8000-000000000003"),
                column: "updated_at",
                value: new DateTimeOffset(new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_templates",
                keyColumn: "id",
                keyValue: new Guid("0a5e0000-0000-4000-8000-000000000005"),
                column: "updated_at",
                value: new DateTimeOffset(new DateTime(2026, 7, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.UpdateData(
                schema: "core",
                table: "volume_offerings",
                keyColumn: "id",
                keyValue: "standard",
                columns: new[] { "is_customized", "size_gb" },
                values: new object[] { true, 0 });
        }
    }
}
