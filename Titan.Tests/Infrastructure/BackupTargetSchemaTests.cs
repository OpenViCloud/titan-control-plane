using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Infrastructure.Persistence;
using BackupTargetAggregate = Titan.Domain.Aggregates.Platform.BackupTarget.BackupTarget;

namespace Titan.Tests.Infrastructure;

// "One backup target" is a DB guard (the primary key is the aggregate's fixed id), not application code. The InMemory
// provider does not enforce it, so this pins the model metadata the migration depends on.
public class BackupTargetSchemaTests
{
    private static IEntityType Target()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(BackupTargetAggregate))!;
    }

    [Fact]
    public void BackupTarget_IsMappedToTheBackupTargetsTable()
    {
        Assert.Equal("backup_targets", Target().GetTableName());
    }

    [Fact]
    public void TheIdIsTheOnlyKey_AndIsNeverGenerated()
    {
        var key = Assert.Single(Target().GetKeys());

        Assert.Equal([nameof(BackupTargetAggregate.Id)], key.Properties.Select(p => p.Name).ToArray());
        Assert.Equal(ValueGenerated.Never, Target().FindProperty(nameof(BackupTargetAggregate.Id))!.ValueGenerated);
    }

    [Theory]
    [InlineData(nameof(BackupTargetAggregate.Endpoint), "endpoint")]
    [InlineData(nameof(BackupTargetAggregate.Region), "region")]
    [InlineData(nameof(BackupTargetAggregate.Bucket), "bucket")]
    [InlineData(nameof(BackupTargetAggregate.AccessKeyId), "access_key_id")]
    [InlineData(nameof(BackupTargetAggregate.SecretCipher), "secret_cipher")]
    [InlineData(nameof(BackupTargetAggregate.UpdatedAt), "updated_at")]
    public void EveryColumn_IsRequired_AndNamedInSnakeCase(string property, string column)
    {
        var p = Target().FindProperty(property)!;

        Assert.False(p.IsNullable);
        Assert.Equal(column, p.GetColumnName());
    }

    // The secret column holds ciphertext bytes, never text a reader of the table could use.
    [Fact]
    public void TheSecretColumn_IsBytes()
    {
        Assert.Equal(typeof(byte[]), Target().FindProperty(nameof(BackupTargetAggregate.SecretCipher))!.ClrType);
    }
}
