using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of the migration that drops the columns nothing read: the migrated schema holds none of
/// them. A column that came back through the model would be written on every save and read by no one.
/// Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class DroppedColumnsDbTests(PostgresFixture pg)
{
    [Theory]
    [InlineData("vm_offerings", "cpu_units")]
    [InlineData("volume_offerings", "size_gb")]
    [InlineData("volume_offerings", "is_customized")]
    [InlineData("backups", "observed_at")]
    [InlineData("provider_commands", "finished_at")]
    [InlineData("wallets", "created_at")]
    [InlineData("wallets", "updated_at")]
    [InlineData("vm_templates", "updated_at")]
    [InlineData("vm_template_clusters", "updated_at")]
    [InlineData("tenants", "email")]
    public async Task TheMigratedSchema_DoesNotHoldTheColumn(string table, string column)
    {
        await using TitanDbContext db = pg.NewDbContext();
        DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'core' AND table_name = @table AND column_name = @column";
        AddParameter(command, "table", table);
        AddParameter(command, "column", column);

        long found = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(0, found);
    }

    /// The table is still there: a typo in a table name above would make its row pass for the wrong reason.
    [Theory]
    [InlineData("vm_offerings")]
    [InlineData("volume_offerings")]
    [InlineData("backups")]
    [InlineData("provider_commands")]
    [InlineData("wallets")]
    [InlineData("vm_templates")]
    [InlineData("vm_template_clusters")]
    [InlineData("tenants")]
    public async Task TheMigratedSchema_StillHoldsTheTable(string table)
    {
        await using TitanDbContext db = pg.NewDbContext();
        DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'core' AND table_name = @table";
        AddParameter(command, "table", table);

        long found = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(1, found);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
