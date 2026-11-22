using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// The schedule reads a cluster's config rows of the day with ListOfKindSinceAsync
// (titan-ops features/backup/design.md#cluster-backup): the kind, the cluster and the day filter in the SQL EF writes.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ProviderCommandListOfKindDbTests(PostgresFixture pg)
{
    private static ProviderCommand Row(string kind, string clusterId, DateTimeOffset at) =>
        ProviderCommand.Create("test-" + Guid.NewGuid(), kind, clusterId, "{}", at);

    [Fact]
    public async Task ListOfKindSince_ReturnsOnlyTheKindAndClusterAsked_CreatedFromTheInstant_OldestFirst()
    {
        string cluster = "c-" + Guid.NewGuid().ToString("N");
        string otherCluster = "o-" + Guid.NewGuid().ToString("N");
        DateTimeOffset dayStart = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        ProviderCommand yesterday = Row(ProviderCommandKinds.ClusterBackup, cluster, dayStart.AddHours(-1));
        ProviderCommand atStart = Row(ProviderCommandKinds.ClusterBackup, cluster, dayStart);
        ProviderCommand later = Row(ProviderCommandKinds.ClusterBackup, cluster, dayStart.AddHours(7));
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.ProviderCommands.AddRange(
                later, yesterday, atStart,
                Row(ProviderCommandKinds.ClusterBackup, otherCluster, dayStart.AddHours(2)),
                Row(ProviderCommandKinds.VmBackup, cluster, dayStart.AddHours(2)));
            await seed.SaveChangesAsync();
        }

        await using TitanDbContext read = pg.NewDbContext();
        var found = await new ProviderCommandRepository(read)
            .ListOfKindSinceAsync(ProviderCommandKinds.ClusterBackup, cluster, dayStart, CancellationToken.None);

        Assert.Equal(new[] { atStart.Id, later.Id }, found.Select(c => c.Id));
    }
}
