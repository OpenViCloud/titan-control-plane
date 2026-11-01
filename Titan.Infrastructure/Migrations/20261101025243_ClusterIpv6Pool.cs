using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// Which network holds each /64 block of a cluster, and when each was given back, moves from the networks table to
    /// one pool per cluster (titan-ops features/network/design.md#ipv6-public). The columns on networks stay for now:
    /// the code before this migration still reads them, and the drop comes in a later migration, after a release has
    /// carried this one. Down drops the pool tables; the networks table still holds everything they were filled from.
    public partial class ClusterIpv6Pool : Migration
    {
        /// One pool per cluster that has a network row with a number. A block held by a network that is not Deleted is
        /// held by it (the unique index on networks lets at most one); otherwise it was given back, at the latest time
        /// any deleted network recorded, or with no time when none did.
        public const string BackfillSql = @"
INSERT INTO core.cluster_ipv6_pools (id, cluster_id, version)
SELECT gen_random_uuid(), cluster_id, 0
FROM core.networks
WHERE public_ipv6_index IS NOT NULL
GROUP BY cluster_id
ON CONFLICT (cluster_id) DO NOTHING;

INSERT INTO core.cluster_ipv6_block64_holdings (pool_id, block64_number, holder_network_id, given_back_at)
SELECT p.id,
       n.public_ipv6_index,
       (array_agg(n.id) FILTER (WHERE n.current_existence <> 'Deleted'))[1],
       CASE WHEN bool_or(n.current_existence <> 'Deleted') THEN NULL ELSE max(n.public_ipv6_released_at) END
FROM core.networks n
JOIN core.cluster_ipv6_pools p ON p.cluster_id = n.cluster_id
WHERE n.public_ipv6_index IS NOT NULL
GROUP BY p.id, n.public_ipv6_index
ON CONFLICT DO NOTHING;";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
                    holder_network_id = table.Column<Guid>(type: "uuid", nullable: true),
                    given_back_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
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

            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cluster_ipv6_block64_holdings",
                schema: "core");

            migrationBuilder.DropTable(
                name: "cluster_ipv6_pools",
                schema: "core");
        }
    }
}
