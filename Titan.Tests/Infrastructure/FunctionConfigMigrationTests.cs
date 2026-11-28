using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Titan.Infrastructure.Migrations;

namespace Titan.Tests.Infrastructure;

// Every version uploaded before FunctionConfig gains the two jsonb columns NOT NULL: without the defaults the migration
// fails on a table with rows, and a version read back would have no variables object to deploy
// (titan-ops features/ovc-function/design.md#function-config).
public class FunctionConfigMigrationTests
{
    [Theory]
    [InlineData("env", "'{}'::jsonb")]
    [InlineData("secret_refs", "'[]'::jsonb")]
    public void ExistingVersions_ReadAsEmpty(string column, string defaultSql)
    {
        AddColumnOperation added = new FunctionConfig().UpOperations
            .OfType<AddColumnOperation>()
            .Single(op => op.Name == column);

        Assert.Equal(("function_versions", "jsonb", false, defaultSql), (added.Table, added.ColumnType, added.IsNullable, added.DefaultValueSql));
    }

    [Fact]
    public void TheScheduleOfAnExistingVersion_IsNone()
    {
        AddColumnOperation added = new FunctionConfig().UpOperations.OfType<AddColumnOperation>().Single(op => op.Name == "schedule");

        Assert.True(added.IsNullable);
        Assert.Null(added.DefaultValueSql);
    }
}
