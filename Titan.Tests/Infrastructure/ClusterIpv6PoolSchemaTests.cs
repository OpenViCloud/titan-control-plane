using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The InMemory provider enforces no key and no index, so the model metadata the migration comes from is pinned here.
// Two network creates in one cluster meet at the pool's version, a first allocation of a cluster at its unique
// cluster_id, and a network holding two blocks at the holder index;
// ErrorStatus finds the clash by these names.
public class ClusterIpv6PoolSchemaTests
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
    public void ThePoolsVersion_IsTheConcurrencyToken_SoTheLaterCommitOfTwoLoadsLoses()
    {
        IEntityType pool = Model().FindEntityType(typeof(ClusterIpv6Pool))!;

        Assert.True(pool.FindProperty(nameof(ClusterIpv6Pool.Version))!.IsConcurrencyToken);
    }

    [Fact]
    public void AClusterHasOnePool_ByAUniqueClusterId()
    {
        IEntityType pool = Model().FindEntityType(typeof(ClusterIpv6Pool))!;

        IIndex index = pool.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(ClusterIpv6Pool.ClusterId)]));
        Assert.True(index.IsUnique);
        Assert.Equal(ClusterIpv6PoolConfiguration.ClusterIndexName, index.GetDatabaseName());
    }

    [Fact]
    public void ABlock64IsOneRowPerPoolAndNumber()
    {
        IEntityType holding = Model().FindEntityType(typeof(Ipv6Block64Holding))!;

        IKey key = holding.FindPrimaryKey()!;
        Assert.Equal([nameof(Ipv6Block64Holding.PoolId), nameof(Ipv6Block64Holding.Number)], key.Properties.Select(p => p.Name));
        Assert.Equal(Ipv6Block64HoldingConfiguration.HoldingKeyName, key.GetName());
    }

    [Fact]
    public void ANetworkHoldsAtMostOneBlock64_ByAPartialUniqueHolderIndex()
    {
        IEntityType holding = Model().FindEntityType(typeof(Ipv6Block64Holding))!;

        IIndex index = holding.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(Ipv6Block64Holding.HolderNetworkId)]));
        Assert.True(index.IsUnique);
        Assert.Equal("holder_network_id IS NOT NULL", index.GetFilter());
        Assert.Equal(Ipv6Block64HoldingConfiguration.HolderIndexName, index.GetDatabaseName());
    }
}
