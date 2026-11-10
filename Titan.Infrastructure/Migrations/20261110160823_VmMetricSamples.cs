using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VmMetricSamples : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "vm_metric_samples",
                schema: "core",
                columns: table => new
                {
                    vm_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sampled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    cpu_seconds = table.Column<double>(type: "double precision", nullable: false),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_available_bytes = table.Column<long>(type: "bigint", nullable: true),
                    root_fs_size_bytes = table.Column<long>(type: "bigint", nullable: true),
                    root_fs_avail_bytes = table.Column<long>(type: "bigint", nullable: true),
                    disk_read_bytes = table.Column<long>(type: "bigint", nullable: false),
                    disk_written_bytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_vm_metric_samples", x => new { x.vm_id, x.sampled_at });
                    table.ForeignKey(
                        name: "FK_vm_metric_samples_vms_vm_id",
                        column: x => x.vm_id,
                        principalSchema: "core",
                        principalTable: "vms",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "vm_metric_samples",
                schema: "core");
        }
    }
}
