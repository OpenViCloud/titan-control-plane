using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/provider-and-worker.md#provider-command-table — InMemory enforces neither the unique key nor the
// version token, so this pins the metadata the migration comes from; ProviderCommandLeaseDbTests measures them on
// Postgres.
public class ProviderCommandSchemaTests
{
    private static IEntityType EntityType()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        IEntityType? entity = db.Model.FindEntityType(typeof(ProviderCommand));
        Assert.NotNull(entity);
        return entity;
    }

    [Fact]
    public void TableName_IsProviderCommands()
    {
        Assert.Equal("provider_commands", EntityType().GetTableName());
    }

    [Fact]
    public void Key_IsUnique()
    {
        bool uniqueOnKey = EntityType().GetIndexes().Any(i =>
            i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(ProviderCommand.Key));
        Assert.True(uniqueOnKey);
    }

    [Fact]
    public void LeaseId_IsUnique()
    {
        bool uniqueOnLease = EntityType().GetIndexes().Any(i =>
            i.IsUnique && i.Properties.Count == 1 && i.Properties[0].Name == nameof(ProviderCommand.LeaseId));
        Assert.True(uniqueOnLease);
    }

    [Fact]
    public void Version_IsTheConcurrencyToken()
    {
        IProperty? version = EntityType().FindProperty(nameof(ProviderCommand.Version));
        Assert.NotNull(version);
        Assert.True(version.IsConcurrencyToken);
    }
}
