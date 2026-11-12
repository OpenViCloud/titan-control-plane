using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MeterTicks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "meter_ticks",
                schema: "core",
                columns: table => new
                {
                    meter = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_meter_ticks", x => x.meter);
                });

            // Written by hand: the three meters, registered now and never ticked, so /healthz/meters counts its window from
            // this deploy until each first ticks (the names are the IMeter.Name of ComputeMeterService, DiskMeterService and
            // SnapshotMeterService).
            migrationBuilder.Sql(
                "INSERT INTO core.meter_ticks (meter, registered_at) VALUES ('Compute', now()), ('Disk', now()), ('Snapshot', now())");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "meter_ticks",
                schema: "core");
        }
    }
}
