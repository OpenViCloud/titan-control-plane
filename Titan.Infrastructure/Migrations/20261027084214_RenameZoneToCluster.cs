using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameZoneToCluster : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Renames only: every row keeps its value (singapore01, local-mac). EF scaffolds the template table as
            // drop + create, which would empty it.
            migrationBuilder.RenameColumn(
                name: "zone_id",
                schema: "core",
                table: "volumes",
                newName: "cluster_id");

            migrationBuilder.RenameColumn(
                name: "zone_id",
                schema: "core",
                table: "vms",
                newName: "cluster_id");

            migrationBuilder.RenameColumn(
                name: "zone_id",
                schema: "core",
                table: "provider_commands",
                newName: "cluster_id");

            migrationBuilder.RenameColumn(
                name: "zone_id",
                schema: "core",
                table: "networks",
                newName: "cluster_id");

            migrationBuilder.RenameColumn(
                name: "zone_id",
                schema: "core",
                table: "load_balancers",
                newName: "cluster_id");

            migrationBuilder.RenameIndex(
                name: "ix_networks_zone_id_public_ipv6_index",
                schema: "core",
                table: "networks",
                newName: "ix_networks_cluster_id_public_ipv6_index");

            migrationBuilder.RenameTable(
                name: "vm_template_zones",
                schema: "core",
                newName: "vm_template_clusters",
                newSchema: "core");

            migrationBuilder.RenameColumn(
                name: "zone_id",
                schema: "core",
                table: "vm_template_clusters",
                newName: "cluster_id");

            migrationBuilder.Sql("""ALTER TABLE core.vm_template_clusters RENAME CONSTRAINT "PK_vm_template_zones" TO "PK_vm_template_clusters";""");
            migrationBuilder.Sql("""ALTER TABLE core.vm_template_clusters RENAME CONSTRAINT "FK_vm_template_zones_vm_templates_template_id" TO "FK_vm_template_clusters_vm_templates_template_id";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""ALTER TABLE core.vm_template_clusters RENAME CONSTRAINT "FK_vm_template_clusters_vm_templates_template_id" TO "FK_vm_template_zones_vm_templates_template_id";""");
            migrationBuilder.Sql("""ALTER TABLE core.vm_template_clusters RENAME CONSTRAINT "PK_vm_template_clusters" TO "PK_vm_template_zones";""");

            migrationBuilder.RenameColumn(
                name: "cluster_id",
                schema: "core",
                table: "vm_template_clusters",
                newName: "zone_id");

            migrationBuilder.RenameTable(
                name: "vm_template_clusters",
                schema: "core",
                newName: "vm_template_zones",
                newSchema: "core");

            migrationBuilder.RenameIndex(
                name: "ix_networks_cluster_id_public_ipv6_index",
                schema: "core",
                table: "networks",
                newName: "ix_networks_zone_id_public_ipv6_index");

            migrationBuilder.RenameColumn(
                name: "cluster_id",
                schema: "core",
                table: "volumes",
                newName: "zone_id");

            migrationBuilder.RenameColumn(
                name: "cluster_id",
                schema: "core",
                table: "vms",
                newName: "zone_id");

            migrationBuilder.RenameColumn(
                name: "cluster_id",
                schema: "core",
                table: "provider_commands",
                newName: "zone_id");

            migrationBuilder.RenameColumn(
                name: "cluster_id",
                schema: "core",
                table: "networks",
                newName: "zone_id");

            migrationBuilder.RenameColumn(
                name: "cluster_id",
                schema: "core",
                table: "load_balancers",
                newName: "zone_id");
        }
    }
}
