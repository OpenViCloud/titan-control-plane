using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The InMemory provider enforces no index, so the model metadata the migration comes from is pinned here. Two
// network creates that picked the same free /64 meet at this index (titan-ops infra/incus/design.md#ipv6-address-plan);
// its condition must say what NetworkRepository.ListPublicIpv6IndexesInClusterAsync says, or the handler's "free"
// /64 would fail here every time. ErrorStatus finds the clash by the index name.
public class NetworkSchemaTests
{
    private static IIndex PublicIpv6Index()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(Network))!.GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Network.ClusterId), nameof(Network.PublicIpv6Index)]));
    }

    [Fact]
    public void PublicIpv6Index_IsUniquePerCluster_AmongNetworksNotDeleted()
    {
        IIndex index = PublicIpv6Index();

        Assert.True(index.IsUnique);
        Assert.Equal("public_ipv6_index IS NOT NULL AND current_existence <> 'Deleted'", index.GetFilter());
        Assert.Equal(NetworkConfiguration.PublicIpv6IndexName, index.GetDatabaseName());
    }
}
