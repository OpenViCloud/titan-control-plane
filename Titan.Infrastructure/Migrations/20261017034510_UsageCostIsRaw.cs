using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    // usage_records.cost keeps the raw product of one record; whole VND happens once, on the invoice line
    // (titan-ops architecture/money-and-wallet.md#round-once). Up widens bigint to numeric: existing rows keep the rounded value
    // they were written with, nothing is recomputed. Down narrows back and rounds every fractional cost away.
    /// <inheritdoc />
    public partial class UsageCostIsRaw : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "cost",
                schema: "core",
                table: "usage_records",
                type: "numeric(18,10)",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "cost",
                schema: "core",
                table: "usage_records",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,10)");
        }
    }
}
