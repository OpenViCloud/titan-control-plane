using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// The name a usage record and its invoice line keep may be as long as the longest name of a metered resource: 100
    /// (a volume or a snapshot), not 64. Shorter, the meter's insert fails on that row, which stops that meter's run and
    /// every later one at the same row, and invoice generation fails on that tenant's invoice and stops before the
    /// tenants after it
    /// (titan-ops features/billing/metering/design.md#meters). Widening rewrites no row. Down narrows back to 64 and
    /// fails once a longer name is stored: rolling back then needs those names cut first.
    public partial class WidenResourceName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "resource_name",
                schema: "core",
                table: "usage_records",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<string>(
                name: "resource_name",
                schema: "core",
                table: "invoice_items",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "resource_name",
                schema: "core",
                table: "usage_records",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AlterColumn<string>(
                name: "resource_name",
                schema: "core",
                table: "invoice_items",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);
        }
    }
}
