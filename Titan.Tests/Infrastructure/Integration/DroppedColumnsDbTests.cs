using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of the migration that drops the columns the control plane did not read: the migrated
/// schema holds none of them. A column that came back through the model would be written on every save and read by no one.
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
    public async Task TheMigratedSchema_HoldsTheTable_WithoutTheColumn(string table, string column)
    {
        await using TitanDbContext db = pg.NewDbContext();
        DbConnection connection = db.Database.GetDbConnection();
        await connection.OpenAsync();

        // A table name that matches nothing would count no column either, so the table is counted first.
        long tables = await CountAsync(connection,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'core' AND table_name = @table",
            ("table", table));
        long columns = await CountAsync(connection,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'core' AND table_name = @table AND column_name = @column",
            ("table", table), ("column", column));

        Assert.Equal(1, tables);
        Assert.Equal(0, columns);
    }

    private static async Task<long> CountAsync(DbConnection connection, string sql, params (string Name, string Value)[] parameters)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, string value) in parameters)
        {
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
