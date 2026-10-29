using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Options;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Application.Queries.Platform.ProviderCommand;
using Titan.Application.Queries.Platform.ProviderCommand.Contracts;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Domain.Aggregates.Platform.ProviderCommand;
using Titan.Domain.Repositories.Catalog;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// The worker names its cluster as clusterId on every route and the lease
// (titan-ops architecture/provider-and-worker.md#contract-changes).
public class ClusterIdContractTests
{
    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseInMemoryDatabase($"cluster-id-{Guid.NewGuid()}").Options);

    private static ListReconcilableTemplatesEndpoint TemplateEndpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string query) =>
        Factory.Create<ListReconcilableTemplatesEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = "secret";
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query);
            },
            new ListReconcilableTemplatesQueryHandler(new VmTemplateRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    [Fact]
    public async Task AReconcilerAskingByClusterId_GetsThatClustersRows_AndItsHeartbeat()
    {
        using var db = NewDb();
        var template = VmTemplate.Create("t", null, "img", 10, "m");
        template.RequestSyncIn("mock");
        template.MarkCreatedIn("mock", "205");
        db.VmTemplates.Add(template);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = TemplateEndpoint(db, heartbeat, "?clusterId=mock");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal("205", Assert.Single(ep.Response).ExternalTemplateId);
        Assert.NotNull(heartbeat.Snapshot()["templates/mock"].LastPollAt);
    }

    [Fact]
    public async Task AReconcilerAskingWithoutAClusterId_Is400_AndRecordsNoPoll()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = TemplateEndpoint(db, heartbeat, "?name=mock");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(400, ep.HttpContext.Response.StatusCode);
        Assert.Empty(heartbeat.Snapshot());
    }

    // Every reconcile route reads the cluster the same way, from clusterId.
    [Fact]
    public void EveryInternalRoute_ReadsTheClusterThroughClusterQuery()
    {
        string folder = Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "Endpoints", "Internal");
        var readersBypassingClusterQuery = Directory.GetFiles(folder, "*.cs")
            .Where(f => File.ReadAllText(f).Contains("Query<string>(\"clusterId\""))
            .Select(Path.GetFileName)
            .ToList();
        var readersOfClusterQuery = Directory.GetFiles(folder, "ListReconcilable*Endpoint.cs")
            .Where(f => File.ReadAllText(f).Contains("ClusterQuery.From("))
            .ToList();

        Assert.Empty(readersBypassingClusterQuery);
        Assert.Equal(7, readersOfClusterQuery.Count);
    }

    [Fact]
    public async Task ATemplateReport_WithItsClusterId_IsRecorded()
    {
        var templates = new Mock<IVmTemplateRepository>();
        var clusters = new ClusterOptions { Clusters = [new ClusterConfig { Id = "mock", Name = "Mock" }] };
        var handler = new UpdateTemplateStatusCommandHandler(templates.Object, clusters, NullLogger<UpdateTemplateStatusCommandHandler>.Instance);
        var ep = Factory.Create<UpdateTemplateStatusEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = "secret";
                ctx.Request.RouteValues["templateId"] = Guid.NewGuid().ToString();
            },
            handler, ConfigWithToken("secret"));

        var notFound = await Assert.ThrowsAsync<Titan.Domain.Exceptions.NotFoundException>(() =>
            ep.HandleAsync(new UpdateTemplateStatusRequest(Report: "Created", ClusterId: "mock", ExternalId: "tpl-205"), CancellationToken.None));

        // Past the body check and the cluster check: the handler looked the template up, and found none.
        Assert.Equal("TEMPLATE_NOT_FOUND", notFound.Code);
        templates.Verify(t => t.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ALease_CarriesTheClusterId()
    {
        var repo = new FakeProviderCommandRepository();
        var t0 = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
        repo.Add(ProviderCommand.Create("vm-provision-1", ProviderCommandKinds.VmCreate, "mock", "{}", ProviderCommandRerun.AfterAnyFinish, t0));
        Guid leaseId = Guid.NewGuid();
        await new Titan.Application.Commands.Platform.ProviderCommand.LeaseProviderCommandCommandHandler(repo, new FixedClock(t0))
            .HandleAsync(new Titan.Application.Commands.Platform.ProviderCommand.Contracts.LeaseProviderCommandCommand(leaseId, [ProviderCommandKinds.VmCreate]));

        LeasedProviderCommandResult leased = await new GetLeasedProviderCommandQueryHandler(repo).HandleAsync(new GetLeasedProviderCommandQuery(leaseId));

        Assert.Equal("mock", leased.ClusterId);
    }

    private static ListInternalClustersEndpoint ClustersEndpoint(string token) =>
        Factory.Create<ListInternalClustersEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = token,
            new ClusterOptions
            {
                Clusters =
                [
                    new ClusterConfig { Id = "singapore01", Name = "Singapore 01", Endpoint = "https://localhost:8443", ServerFingerprint = new string('a', 64) },
                    new ClusterConfig { Id = "local-mac", Name = "Local Mac", Endpoint = "https://titan-incus:8443", ServerFingerprint = new string('b', 64) },
                ],
            },
            ConfigWithToken("secret"));

    // The worker lists the clusters it reconciles here, not through the public catalog route.
    [Fact]
    public async Task TheWorker_ListsTheConfiguredClusters_IdAndNameOnly()
    {
        var ep = ClustersEndpoint("secret");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(["singapore01", "local-mac"], ep.Response.Select(c => c.Id).ToList());
        Assert.Equal("Local Mac", ep.Response[1].Name);
    }

    [Fact]
    public async Task TheClusterList_WithoutTheInternalToken_Is401()
    {
        var ep = ClustersEndpoint("wrong");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
    }
}
