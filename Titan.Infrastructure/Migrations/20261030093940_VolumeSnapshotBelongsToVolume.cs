using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VolumeSnapshotBelongsToVolume : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_volume_snapshots_volumes_volume_id",
                schema: "core",
                table: "volume_snapshots",
                column: "volume_id",
                principalSchema: "core",
                principalTable: "volumes",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_volume_snapshots_volumes_volume_id",
                schema: "core",
                table: "volume_snapshots");
        }
    }
}
