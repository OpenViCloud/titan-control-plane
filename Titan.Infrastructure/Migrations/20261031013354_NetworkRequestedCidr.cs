using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Titan.Infrastructure.Migrations
{
    /// The range the customer asked for gets its own column, held from the request on
    /// (titan-ops features/network/user-stories.md#us-net-intent-02); cidr and gateway keep what the worker reported, and an
    /// address the report did not carry is NULL — rows written as '' before this are NULL too. Down drops the column only:
    /// NULL for '' is not undone, and the code before this migration reads NULL as "no address" as well.
    public partial class NetworkRequestedCidr : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "requested_cidr",
                schema: "core",
                table: "networks",
                type: "text",
                nullable: true);

            migrationBuilder.Sql("UPDATE core.networks SET cidr = NULL WHERE cidr = '';");
            migrationBuilder.Sql("UPDATE core.networks SET gateway = NULL WHERE gateway = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "requested_cidr",
                schema: "core",
                table: "networks");
        }
    }
}
