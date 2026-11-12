using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Networking.Network;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the network pool reconciler: the list it asks for (GET /internal/reconcile/networks),
// the heartbeat that list request leaves behind, and the liveness verdict on that heartbeat.
public class NetworkReconcilerTests
{
    private const string Cluster = "vn-hcm-1";

    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"network-reconcile-{Guid.NewGuid()}")
            .Options);

    private static Network ActiveIn(string cluster, string name)
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), name, cluster);
        network.MarkActive($"ext-{name}", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    // ── The list: only what reached the provider and is not yet confirmed gone, in this cluster ────

    [Fact]
    public async Task List_HoldsProvisionedNetworksOfTheCluster_IncludingErroredNotFoundAndDeleteInFlight()
    {
        using var db = NewDb();
        var active = ActiveIn(Cluster, "active");
        var errored = ActiveIn(Cluster, "errored");
        errored.MarkObserved(NetworkStatus.Errored);
        var lost = ActiveIn(Cluster, "lost");
        lost.MarkNotFound();
        var leaving = ActiveIn(Cluster, "leaving");
        leaving.Delete();
        var gone = ActiveIn(Cluster, "gone");
        gone.Delete();
        gone.MarkDeleted();
        var creating = TestAggregates.Network(TestTenant.WithRoomToSpare(), "creating", Cluster);
        var otherCluster = ActiveIn("vn-han-1", "elsewhere");
        db.Networks.AddRange(active, errored, lost, leaving, gone, creating, otherCluster);
        await db.SaveChangesAsync();

        var names = (await new NetworkRepository(db).ListReconcilableAsync(Cluster)).Select(n => n.Name.Value).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "active", "errored", "leaving", "lost" }, names);
    }

    // ── The endpoint ───────────────────────────────────────────────────────

    [Fact]
    public async Task Endpoint_WithToken_ReturnsTheList_WithWhatTitanRecorded_AndRecordsTheHeartbeat()
    {
        using var db = NewDb();
        var leaving = ActiveIn(Cluster, "leaving");
        leaving.Delete();
        db.Networks.Add(leaving);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat(db);
        var ep = Factory.Create<ListReconcilableNetworksEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = "secret";
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={Cluster}");
            },
            new ListReconcilableNetworksQueryHandler(new NetworkRepository(db)), heartbeat, ConfigWithToken("secret"));

        await ep.HandleAsync(CancellationToken.None);

        var only = Assert.Single(ep.Response);
        Assert.Equal(leaving.Id, only.Id);
        Assert.Equal("ext-leaving", only.ExternalId);
        Assert.Equal("Created", only.CurrentState);
        Assert.Equal("Exists", only.CurrentExistence);
        Assert.True((await heartbeat.SnapshotAsync(CancellationToken.None)).ContainsKey($"networks/{Cluster}"));
    }

    [Fact]
    public async Task Endpoint_WrongToken_Returns401_AndLeavesNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat(db);
        var ep = Factory.Create<ListReconcilableNetworksEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = "wrong";
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={Cluster}");
            },
            new ListReconcilableNetworksQueryHandler(new NetworkRepository(db)), heartbeat, ConfigWithToken("secret"));

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Empty((await heartbeat.SnapshotAsync(CancellationToken.None)));
    }

    // ── Liveness verdict ───────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Liveness_NoPoll_IsHealthy()
    {
        var result = ReconcilerLivenessHealthCheck.Evaluate(Now, new Dictionary<string, DateTimeOffset>(), 30);
        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void Liveness_RecentPoll_IsHealthy()
    {
        var snapshot = new Dictionary<string, DateTimeOffset>
        {
            ["networks/vn-hcm-1"] = Now.AddMinutes(-3),
        };
        Assert.Equal(HealthStatus.Healthy, ReconcilerLivenessHealthCheck.Evaluate(Now, snapshot, 30).Status);
    }

    [Fact]
    public void Liveness_PollOlderThanTheWindow_IsUnhealthy_AndNamesIt()
    {
        var snapshot = new Dictionary<string, DateTimeOffset>
        {
            ["networks/vn-hcm-1"] = Now.AddMinutes(-31),
            ["networks/vn-han-1"] = Now.AddMinutes(-1),
        };

        var result = ReconcilerLivenessHealthCheck.Evaluate(Now, snapshot, 30);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("networks/vn-hcm-1", result.Description);
        Assert.DoesNotContain("networks/vn-han-1", result.Description);
    }

    // A cluster removed from config, or a kind no longer reconciled, never polls again: its row does not count.
    [Fact]
    public void Liveness_ARowOfAClusterNotConfigured_OrOfAnUnknownKind_DoesNotCount()
    {
        var polls = new Dictionary<string, DateTimeOffset>
        {
            ["networks/gone"] = Now.AddDays(-3),
            ["old-kind/vn-hcm-1"] = Now.AddDays(-3),
            ["networks/vn-hcm-1"] = Now.AddMinutes(-1),
        };

        var expected = ReconcilerLivenessHealthCheck.Expected(polls, ["vn-hcm-1"]);

        Assert.Equal(["networks/vn-hcm-1"], expected.Keys);
        Assert.Equal(HealthStatus.Healthy, ReconcilerLivenessHealthCheck.Evaluate(Now, expected, 30).Status);
    }
}
