using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// A firewall rule is the network's child entity (titan-ops architecture/code-structure.md#child-entity): its row belongs
// to a network row, and the table keeps its name and columns. The InMemory provider does not enforce keys the way
// Postgres does, so this pins the model metadata the migration builds the constraint from.
public class FirewallRuleSchemaTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model;
    }

    [Fact]
    public void StillMappedToTheFirewallRulesTable()
    {
        Assert.Equal("firewall_rules", Model().FindEntityType(typeof(FirewallRule))!.GetTableName());
    }

    // Restrict, not cascade: no row of either table is ever deleted (titan-ops features/network/design.md#firewall-rule-soft-delete),
    // and a rule whose network row is missing is a broken record the database must refuse, not silently drop.
    [Fact]
    public void NetworkId_IsAForeignKeyToNetworks_WithoutCascade()
    {
        var foreignKey = Model().FindEntityType(typeof(FirewallRule))!.GetForeignKeys().Single();

        Assert.Equal(typeof(Network), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(FirewallRule.NetworkId)], foreignKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact] // the network loads its rules through the backing field: no public setter on the collection
    public void Network_OwnsTheRulesNavigation()
    {
        var navigation = Model().FindEntityType(typeof(Network))!.FindNavigation(nameof(Network.FirewallRules))!;

        Assert.Equal(typeof(FirewallRule), navigation.TargetEntityType.ClrType);
        Assert.Equal(PropertyAccessMode.Field, navigation.GetPropertyAccessMode());
        Assert.True(navigation.IsCollection);
    }
}
