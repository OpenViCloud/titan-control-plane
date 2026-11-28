using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// "One name per tenant" and "one number per secret" are keys of the tables, and a version's configuration is two jsonb
// columns every older row reads as empty (titan-ops features/ovc-function/design.md#function-secrets). The InMemory
// provider enforces none of it, so this pins the model metadata the migration builds the tables from.
public class FunctionSecretSchemaTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model;
    }

    private static string ColumnTypeOf(IProperty property) =>
        (string)property.FindAnnotation(RelationalAnnotationNames.ColumnType)!.Value!;

    [Fact]
    public void MappedToTheirTables_InCore()
    {
        IEntityType secret = Model().FindEntityType(typeof(FunctionSecret))!;
        IEntityType version = Model().FindEntityType(typeof(FunctionSecretVersion))!;

        Assert.Equal(("function_secrets", "core"), (secret.GetTableName(), secret.GetSchema()));
        Assert.Equal(("function_secret_versions", "core"), (version.GetTableName(), version.GetSchema()));
    }

    [Fact]
    public void TheName_IsUniqueInTheTenant()
    {
        IIndex index = Model().FindEntityType(typeof(FunctionSecret))!.GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(FunctionSecret.TenantId), nameof(FunctionSecret.Name)]));

        Assert.True(index.IsUnique);
        Assert.Null(index.GetFilter());
    }

    [Fact] // a key of its own would let one secret hold two version 3s
    public void AVersionsKey_IsTheSecretAndTheNumber()
    {
        IKey key = Model().FindEntityType(typeof(FunctionSecretVersion))!.FindPrimaryKey()!;

        Assert.Equal([nameof(FunctionSecretVersion.SecretId), nameof(FunctionSecretVersion.Number)], key.Properties.Select(p => p.Name));
    }

    [Fact] // a hard delete of the secret takes its versions
    public void Versions_BelongToTheirSecret_DeletedWithIt()
    {
        IForeignKey foreignKey = Model().FindEntityType(typeof(FunctionSecretVersion))!.GetForeignKeys().Single();

        Assert.Equal(typeof(FunctionSecret), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Fact] // only the ciphertext: no column could hold a value in clear
    public void AVersion_HoldsTheCiphertext_NotTheValue()
    {
        string[] columns = Model().FindEntityType(typeof(FunctionSecretVersion))!.GetProperties().Select(p => p.GetColumnName()).Order().ToArray();

        Assert.Equal(["ciphertext", "created_at", "created_by", "key_id", "nonce", "number", "secret_id", "value_bytes"], columns);
    }

    [Theory]
    [InlineData(nameof(FunctionVersion.Env), "env", "'{}'::jsonb")]
    [InlineData(nameof(FunctionVersion.SecretRefs), "secret_refs", "'[]'::jsonb")]
    public void AVersionsConfiguration_IsJsonb_EmptyForTheRowsBefore(string property, string column, string defaultSql)
    {
        IProperty mapped = Model().FindEntityType(typeof(FunctionVersion))!.FindProperty(property)!;

        Assert.Equal((column, "jsonb", defaultSql, false), (mapped.GetColumnName(), ColumnTypeOf(mapped), mapped.GetDefaultValueSql(), mapped.IsNullable));
    }

    [Fact]
    public void AVersionsSchedule_IsNullableText()
    {
        IProperty mapped = Model().FindEntityType(typeof(FunctionVersion))!.FindProperty(nameof(FunctionVersion.Schedule))!;

        Assert.Equal(("schedule", "text", true), (mapped.GetColumnName(), ColumnTypeOf(mapped), mapped.IsNullable));
    }
}
