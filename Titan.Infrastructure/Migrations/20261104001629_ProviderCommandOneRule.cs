using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// One rule for a repeat of every provider command key — an open or Done row absorbs it, a Failed one reopens
    /// (titan-ops architecture/resource-lifecycle.md#one-key-per-request) — so the per-row rerun column goes; no row
    /// changes otherwise. An image from before this migration writes that column on every enqueue: rolling one back needs
    /// this migration's Down first. The index (status, created_at) serves the retention pass that deletes old finished rows.
    public partial class ProviderCommandOneRule : Migration
    {
        /// The mode the image just before this migration writes for each key: the two backups, keyed by their day, rerun
        /// only after a failure; every other key after any finish. Older rows written with another mode get their key's
        /// mode too, not the one they had.
        private const string RestoreTheRerunOfEachRowSql = @"
UPDATE core.provider_commands SET rerun = 'AfterFailureOnly'
WHERE key LIKE 'vm-backup-%' OR key LIKE 'volume-backup-%';";

        private const string DropTheRerunDefaultSql = @"
ALTER TABLE core.provider_commands ALTER COLUMN rerun DROP DEFAULT;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rerun",
                schema: "core",
                table: "provider_commands");

            migrationBuilder.CreateIndex(
                name: "IX_provider_commands_status_created_at",
                schema: "core",
                table: "provider_commands",
                columns: new[] { "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_provider_commands_status_created_at",
                schema: "core",
                table: "provider_commands");

            migrationBuilder.AddColumn<string>(
                name: "rerun",
                schema: "core",
                table: "provider_commands",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "AfterAnyFinish");
            migrationBuilder.Sql(RestoreTheRerunOfEachRowSql);
            // The column had no default before this migration.
            migrationBuilder.Sql(DropTheRerunDefaultSql);
        }
    }
}
