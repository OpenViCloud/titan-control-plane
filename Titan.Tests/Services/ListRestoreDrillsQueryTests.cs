using Titan.Application.Options;
using Titan.Application.Queries.Admin.Backup;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The Backups page's "latest verified restore": read from the restore.drill rows, one line per configured cluster
// (titan-ops features/backup/design.md#restore-drill).
public class ListRestoreDrillsQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeProviderCommandRepository _rows = new();

    private Task<IReadOnlyList<RestoreDrillQueryResult>> RunAsync(params string[] clusters) =>
        new ListRestoreDrillsQueryHandler(_rows, new FixedClock(Now),
                new ClusterOptions { Clusters = (clusters.Length == 0 ? ["mock"] : clusters).Select(id => new ClusterConfig { Id = id, Name = "Cluster " + id }).ToList() })
            .HandleAsync(new ListRestoreDrillsQuery());

    // A drill row asked at `at` on the cluster, ended Done or Failed with `error`, or left open.
    private ProviderCommand Drill(string cluster, string kind, string name, DateTimeOffset at, string? endedWith = "done")
    {
        string payload = $$"""{"kind":"{{kind}}","resourceId":"{{Guid.NewGuid()}}","externalId":"x","name":"{{name}}","projectName":"t-1","tenantId":"{{Guid.NewGuid()}}"}""";
        ProviderCommand row = ProviderCommand.Create($"restore-drill-{cluster}-{at:yyyyMMdd}", ProviderCommandKinds.RestoreDrill, cluster, payload, at, null, null);
        _rows.Rows.Add(row);
        if (endedWith == null)
        {
            return row;
        }
        Guid lease = Guid.NewGuid();
        row.Lease(lease, at);
        if (endedWith == "done")
        {
            row.Complete(lease);
        }
        else
        {
            row.Fail(lease, endedWith, retryable: false, at);
        }
        return row;
    }

    [Fact]
    public async Task WithNoDrillInTheLastThirtyDays_TheStateIsNone()
    {
        Drill("mock", "vm", "web-1", Now.AddDays(-31));

        var result = Assert.Single(await RunAsync());

        Assert.Equal("none", result.State);
        Assert.Equal("mock", result.ClusterId);
        Assert.Equal("Cluster mock", result.ClusterName);
        Assert.Null(result.VerifiedAt);
    }

    [Fact]
    public async Task TheNewestDrillDone_IsTheLatestVerifiedRestore_WithItsTimeKindAndName()
    {
        Drill("mock", "volume", "data-1", Now.AddDays(-2));
        Drill("mock", "vm", "web-1", Now.AddDays(-1));

        var result = Assert.Single(await RunAsync());

        Assert.Equal("verified", result.State);
        Assert.Equal(Now.AddDays(-1), result.VerifiedAt);
        Assert.Equal("vm", result.Kind);
        Assert.Equal("web-1", result.Name);
        Assert.Null(result.Reason);
    }

    // AC-8: a newer failed drill turns the line to "not verified" with the reason, and still says when one last passed.
    [Fact]
    public async Task TheNewestDrillFailed_IsNotVerified_WithItsReason_AndTheLastGoodTime()
    {
        Drill("mock", "vm", "web-1", Now.AddDays(-3));
        Drill("mock", "volume", "data-1", Now.AddDays(-1), endedWith: "import volume vol1: disk full");

        var result = Assert.Single(await RunAsync());

        Assert.Equal("notVerified", result.State);
        Assert.Equal("import volume vol1: disk full", result.Reason);
        Assert.Equal("volume", result.Kind);
        Assert.Equal("data-1", result.Name);
        Assert.Equal(Now.AddDays(-3), result.VerifiedAt);
    }

    [Fact]
    public async Task ADrillStillRunning_IsNotTheAnswer_TheNewestEndedOneIs()
    {
        Drill("mock", "vm", "web-1", Now.AddDays(-1));
        Drill("mock", "volume", "data-1", Now.AddHours(-1), endedWith: null);

        var result = Assert.Single(await RunAsync());

        Assert.Equal("verified", result.State);
        Assert.Equal("web-1", result.Name);
    }

    [Fact]
    public async Task EachConfiguredCluster_HasItsOwnLine()
    {
        Drill("mock", "vm", "web-1", Now.AddDays(-1));

        var results = await RunAsync("mock", "other");

        Assert.Equal(new[] { "verified", "none" }, results.Select(r => r.State).ToArray());
    }
}
