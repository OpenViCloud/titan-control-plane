using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// Who holds which /64, and when each went back, moves from the pool's two tables to one row per static route. The
    /// migration before this one filled the pool from the networks' own columns, so the pool is the only source on
    /// every env. Written by hand in this order: scaffolded, it drops the pool before anything can read it.
    /// A number given back with no time arrives with released_at null, which reads as never used; such a number went
    /// back before the time was recorded, long past the wait the time is for. The columns on networks stay for now:
    /// the drop comes in a later migration, after a release has carried this one.
    public partial class ClusterIpv6Route : Migration
    {
        /// All 255 rows of a cluster that has a pool, so its routes are complete before the control plane starts.
        public const string CopyFromThePoolSql = @"
INSERT INTO core.cluster_ipv6_routes (cluster_id, number, network_id, released_at)
SELECT p.cluster_id, n, h.holder_network_id, h.given_back_at
FROM core.cluster_ipv6_pools p
CROSS JOIN generate_series(1, 255) n
LEFT JOIN core.cluster_ipv6_block64_holdings h ON h.pool_id = p.id AND h.block64_number = n;";

        public const string CopyBackToThePoolSql = @"
INSERT INTO core.cluster_ipv6_pools (id, cluster_id, version)
SELECT gen_random_uuid(), cluster_id, 0
FROM core.cluster_ipv6_routes
GROUP BY cluster_id;

INSERT INTO core.cluster_ipv6_block64_holdings (pool_id, block64_number, holder_network_id, given_back_at)
SELECT p.id, r.number, r.network_id, r.released_at
FROM core.cluster_ipv6_routes r
JOIN core.cluster_ipv6_pools p ON p.cluster_id = r.cluster_id
WHERE r.network_id IS NOT NULL OR r.released_at IS NOT NULL;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cluster_ipv6_routes",
                schema: "core",
                columns: table => new
                {
                    cluster_id = table.Column<string>(type: "text", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    network_id = table.Column<Guid>(type: "uuid", nullable: true),
                    released_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_ipv6_routes", x => new { x.cluster_id, x.number });
                });

            migrationBuilder.CreateIndex(
                name: "IX_cluster_ipv6_routes_network_id",
                schema: "core",
                table: "cluster_ipv6_routes",
                column: "network_id",
                unique: true,
                filter: "network_id IS NOT NULL");

            migrationBuilder.Sql(CopyFromThePoolSql);

            migrationBuilder.DropTable(
                name: "cluster_ipv6_block64_holdings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "cluster_ipv6_pools",
                schema: "core");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cluster_ipv6_pools",
                schema: "core",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    cluster_id = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cluster_ipv6_pools", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cluster_ipv6_block64_holdings",
                schema: "core",
                columns: table => new
                {
                    pool_id = table.Column<Guid>(type: "uuid", nullable: false),
                    block64_number = table.Column<int>(type: "integer", nullable: false),
                    given_back_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    holder_network_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cluster_ipv6_block64_holdings", x => new { x.pool_id, x.block64_number });
                    table.ForeignKey(
                        name: "FK_cluster_ipv6_block64_holdings_cluster_ipv6_pools_pool_id",
                        column: x => x.pool_id,
                        principalSchema: "core",
                        principalTable: "cluster_ipv6_pools",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cluster_ipv6_block64_holdings_holder_network_id",
                schema: "core",
                table: "cluster_ipv6_block64_holdings",
                column: "holder_network_id",
                unique: true,
                filter: "holder_network_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_cluster_ipv6_pools_cluster_id",
                schema: "core",
                table: "cluster_ipv6_pools",
                column: "cluster_id",
                unique: true);

            migrationBuilder.Sql(CopyBackToThePoolSql);

            migrationBuilder.DropTable(
                name: "cluster_ipv6_routes",
                schema: "core");
        }
    }
}
