using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.FunctionRuntime;
using Titan.Application.Options;
using Titan.Application.Queries.Catalog.FunctionRuntime;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Catalog;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// What the runtime endpoints hold of their own (titan-ops features/ovc-function/design.md#runtimes): the admin role on
// both admin routes, sync-all's 202 with no body, and the runtime reconciler's list keyed on clusterId, which records
// its heartbeat only when it names a cluster.
public class FunctionRuntimeEndpointTests
{
    private static SyncFunctionRuntimesCommandHandler SyncHandler()
    {
        var runtimes = new Mock<IFunctionRuntimeRepository>();
        runtimes.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        return new SyncFunctionRuntimesCommandHandler(runtimes.Object, new FunctionClusterOptions(),
            Mock.Of<IProviderCommandQueue>(), NullLogger<SyncFunctionRuntimesCommandHandler>.Instance);
    }

    // Factory.Create already runs Configure() — do not call it again (roles would double up).
    [Fact]
    public void SyncAll_RequiresThePlatformAdminRole_AndNoAnonymousAccess()
    {
        var ep = Factory.Create<SyncFunctionRuntimesEndpoint>(SyncHandler());

        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
    }

    [Fact]
    public void TheAdminList_RequiresThePlatformAdminRole_AndNoAnonymousAccess()
    {
        var ep = Factory.Create<ListAdminFunctionRuntimesEndpoint>(
            new ListAdminFunctionRuntimesQueryHandler(Mock.Of<IFunctionRuntimeRepository>()));

        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
    }

    // titan-ops architecture/http-contract.md#async-actions-return-202: enqueued, not done, when the response leaves.
    [Fact]
    public async Task SyncAll_Answers202_WithAnEmptyBody()
    {
        var ep = Factory.Create<SyncFunctionRuntimesEndpoint>(SyncHandler());
        ep.HttpContext.Response.Body = new MemoryStream();

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(202, ep.HttpContext.Response.StatusCode);
        Assert.Equal(0, ep.HttpContext.Response.Body.Length);
    }

    private static IConfiguration ConfigWithToken() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "secret" })
            .Build();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseInMemoryDatabase($"function-runtimes-{Guid.NewGuid()}").Options);

    private static ListReconcilableFunctionRuntimesEndpoint ReconcileEndpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat,
        string query, string? token = "secret") =>
        Factory.Create<ListReconcilableFunctionRuntimesEndpoint>(
            ctx =>
            {
                if (token != null)
                {
                    ctx.Request.Headers["X-Internal-Token"] = token;
                }
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query);
            },
            new ListReconcilableFunctionRuntimesQueryHandler(new FunctionRuntimeRepository(db)),
            heartbeat, ConfigWithToken());

    [Fact]
    public async Task TheReconcilerList_IsThatClustersRuntimes_WithTheImage_AndRecordsItsHeartbeat()
    {
        using var db = NewDb();
        FunctionRuntime there = FunctionRuntime.Create("go", $"ghcr.io/x/go@sha256:{new string('a', 64)}", "amd64", null);
        there.SyncSucceededIn("fn-dev", there.SyncIn("fn-dev"));
        FunctionRuntime elsewhere = FunctionRuntime.Create("nodejs26", $"ghcr.io/x/node@sha256:{new string('b', 64)}", "amd64", null);
        elsewhere.SyncIn("fn-two");
        db.FunctionRuntimes.AddRange(there, elsewhere);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat(db);
        var ep = ReconcileEndpoint(db, heartbeat, "?clusterId=fn-dev");

        await ep.HandleAsync(CancellationToken.None);

        var row = Assert.Single(ep.Response);
        Assert.Equal((there.Id, there.Image, "Exists"), (row.Id, row.Image, row.CurrentExistence));
        Assert.True((await heartbeat.SnapshotAsync(CancellationToken.None)).ContainsKey("function-runtimes/fn-dev"));
    }

    [Fact]
    public async Task TheReconcilerList_WithoutAClusterId_Is400_AndRecordsNoPoll()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat(db);
        var ep = ReconcileEndpoint(db, heartbeat, "?name=fn-dev");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(400, ep.HttpContext.Response.StatusCode);
        Assert.Empty(await heartbeat.SnapshotAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task TheReconcilerList_WithoutTheToken_Is401_AndRecordsNoPoll(string? token)
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat(db);
        var ep = ReconcileEndpoint(db, heartbeat, "?clusterId=fn-dev", token);

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Empty(await heartbeat.SnapshotAsync(CancellationToken.None));
    }
}
