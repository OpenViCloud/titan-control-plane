using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.FunctionRuntime;
using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of the runtime catalog (titan-ops features/ovc-function/design.md#runtimes), as
/// TemplateRequestsDbTests for templates: the row lock that makes two sync-alls take turns
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request), the stuck-request view joining a waiting sync to its
/// command row with no tenant (§lost-and-stale), and the report and observation through the real repository. The seeded
/// runtimes are the catalog: its own database. Category=Integration — its own CI step.
[Trait("Category", "Integration")]
public sealed class FunctionRuntimeRequestsDbTests : IAsyncLifetime
{
    private static readonly FunctionClusterOptions FunctionClusters = new()
    {
        Clusters = [new FunctionClusterConfig { Id = "fn-a", Name = "A" }, new FunctionClusterConfig { Id = "fn-b", Name = "B" }],
    };

    private readonly TestPostgres _container = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private async Task SyncAllThroughTheHandlerAsync()
    {
        await using TitanDbContext db = NewDbContext();
        var handler = new SyncFunctionRuntimesCommandHandler(new FunctionRuntimeRepository(db), FunctionClusters,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            NullLogger<SyncFunctionRuntimesCommandHandler>.Instance);
        await handler.HandleAsync(new SyncFunctionRuntimesCommand());
    }

    private async Task<List<FunctionRuntime>> RuntimesAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await new FunctionRuntimeRepository(db).ListAllAsync();
    }

    private async Task<List<ProviderCommand>> SyncCommandsAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Set<ProviderCommand>().AsNoTracking()
            .Where(c => c.Kind == ProviderCommandKinds.FunctionRuntimeSync).ToListAsync();
    }

    // The second waits on each runtime's row lock, then sees the first's sync waiting and asks nothing: one request and
    // one command row per (runtime, function cluster), and neither run fails.
    [Fact]
    public async Task TwoSyncAlls_AtOnce_LeaveOneRequest_AndOneCommandRow_PerRuntimePerCluster()
    {
        await Task.WhenAll(SyncAllThroughTheHandlerAsync(), SyncAllThroughTheHandlerAsync());

        List<FunctionRuntime> runtimes = await RuntimesAsync();
        Assert.Equal(["go", "nodejs26", "python3.15"], runtimes.Select(r => r.Name).Order(StringComparer.Ordinal));
        var expected = new List<string>();
        foreach (FunctionRuntime runtime in runtimes)
        {
            Assert.Equal(["fn-a", "fn-b"], runtime.Clusters.Select(c => c.ClusterId).Order(StringComparer.Ordinal));
            foreach (FunctionRuntimeCluster row in runtime.Clusters)
            {
                Assert.Equal(FunctionRuntimeClusterStatus.Syncing, row.Status);
                expected.Add($"function-runtime-sync-{runtime.Id}-{row.ClusterId}-{row.Operation.PendingId}");
            }
        }
        List<ProviderCommand> commands = await SyncCommandsAsync();
        Assert.Equal(expected.Order(StringComparer.Ordinal), commands.Select(c => c.Key).Order(StringComparer.Ordinal));
        Assert.All(commands, c => Assert.Null(c.TenantId));
        Assert.All(commands, c => Assert.Contains(c.ClusterId, new[] { "fn-a", "fn-b" }));
    }

    [Fact]
    public async Task AStuckSync_IsListedWithNoTenant_AndItsCommandRowsStatus()
    {
        await SyncAllThroughTheHandlerAsync();
        Guid runtimeId = (await RuntimesAsync())[0].Id;
        await using (TitanDbContext db = NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.function_runtime_clusters SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE runtime_id = {runtimeId} AND cluster_id = 'fn-a'");
        }

        await using TitanDbContext read = NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest sync = Assert.Single(stuck);
        Assert.Equal(("function-runtime", runtimeId, (Guid?)null, "Sync", "Pending", true),
            (sync.Resource, sync.Id, sync.TenantId, sync.Kind, sync.CommandStatus, sync.CommandIsOpen));
    }

    // The worker's report and the reconciler's observation through the real repository and its lock: the sync ends,
    // the view no longer lists it, and an image later missing reads NotFound.
    [Fact]
    public async Task ADone_EndsTheSync_AndAnObservationOfTheImageMissing_IsNotFound()
    {
        await SyncAllThroughTheHandlerAsync();
        FunctionRuntime runtime = (await RuntimesAsync())[0];
        Guid requestId = runtime.Clusters.Single(c => c.ClusterId == "fn-a").Operation.PendingId!.Value;

        await using (TitanDbContext db = NewDbContext())
        {
            await new EndFunctionRuntimeSyncRequestCommandHandler(new FunctionRuntimeRepository(db), FunctionClusters)
                .HandleAsync(new EndFunctionRuntimeSyncRequestCommand(runtime.Id, "fn-a", requestId, "Done"));
        }
        await using (TitanDbContext db = NewDbContext())
        {
            await new ObserveFunctionRuntimeCommandHandler(new FunctionRuntimeRepository(db), FunctionClusters)
                .HandleAsync(new ObserveFunctionRuntimeCommand(runtime.Id, "fn-a", Exists: false));
        }

        FunctionRuntimeCluster row = (await RuntimesAsync()).Single(r => r.Id == runtime.Id).Clusters.Single(c => c.ClusterId == "fn-a");
        Assert.Equal(FunctionRuntimeClusterStatus.NotFound, row.Status);
        Assert.Null(row.Operation.PendingId);
        await using TitanDbContext read = NewDbContext();
        int waiting = await read.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM core.pending_requests WHERE resource = 'function-runtime' AND id = {runtime.Id} AND cluster_id = 'fn-a'")
            .SingleAsync();
        Assert.Equal(0, waiting);
    }
}
