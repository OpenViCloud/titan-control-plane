using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class WalletSweeps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "wallet_sweeps",
                schema: "core",
                columns: table => new
                {
                    job = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_sweeps", x => x.job);
                });

            // Written by hand: registered, never run. /healthz/wallet measures a sweeper with no pass from this instant, so a CronJob that
            // never runs shows once three of its intervals have gone by.
            migrationBuilder.Sql(
                "INSERT INTO core.wallet_sweeps (job, registered_at) VALUES ('wallet-sweep', now())");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wallet_sweeps",
                schema: "core");
        }
    }
}
