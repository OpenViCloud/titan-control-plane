using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The InMemory provider enforces no key and no index, so the model metadata the migration comes from is pinned here.
public class ClusterIpv6RouteSchemaTests
{
    private static IEntityType RouteType()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(ClusterIpv6Route))!;
    }

    [Fact]
    public void ARouteIsOneRowPerClusterAndNumber()
    {
        IKey key = RouteType().FindPrimaryKey()!;

        Assert.Equal([nameof(ClusterIpv6Route.ClusterId), nameof(ClusterIpv6Route.Number)], key.Properties.Select(p => p.Name));
    }

    [Fact]
    public void ANetworkHoldsAtMostOneRoute_ByAPartialUniqueIndex()
    {
        IIndex index = RouteType().GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(ClusterIpv6Route.NetworkId)]));

        Assert.True(index.IsUnique);
        Assert.Equal("network_id IS NOT NULL", index.GetFilter());
    }
}
