using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WholeVndPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "core",
                table: "snapshot_offerings",
                keyColumn: "id",
                keyValue: "standard",
                column: "price_per_hour",
                value: 2m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "l",
                column: "price_per_hour",
                value: 640m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "m",
                column: "price_per_hour",
                value: 320m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "s",
                column: "price_per_hour",
                value: 170m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "xl",
                column: "price_per_hour",
                value: 1280m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "volume_offerings",
                keyColumn: "id",
                keyValue: "standard",
                column: "price_per_gb_per_hour",
                value: 2m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "core",
                table: "snapshot_offerings",
                keyColumn: "id",
                keyValue: "standard",
                column: "price_per_hour",
                value: 1.8m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "l",
                column: "price_per_hour",
                value: 634.050m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "m",
                column: "price_per_hour",
                value: 311.256m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "s",
                column: "price_per_hour",
                value: 161.388m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "vm_offerings",
                keyColumn: "id",
                keyValue: "xl",
                column: "price_per_hour",
                value: 1268.100m);

            migrationBuilder.UpdateData(
                schema: "core",
                table: "volume_offerings",
                keyColumn: "id",
                keyValue: "standard",
                column: "price_per_gb_per_hour",
                value: 1.57914m);
        }
    }
}
