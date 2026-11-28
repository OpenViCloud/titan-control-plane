using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.FunctionRuntime;
using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;
using Titan.Application.Exceptions;
using Titan.Application.Options;
using Titan.Application.Queries.Catalog.FunctionRuntime;
using Titan.Application.Queries.Catalog.FunctionRuntime.Contracts;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The runtime catalog of OVC Function at the handler (titan-ops features/ovc-function/design.md#runtimes): sync-all asks
// every runtime into every function cluster that needs it, a report ends the sync it names, an observation writes the
// status at rest or what is seen, and the admin and reconciler reads. The shape is the VM template's
// (TemplateServiceTests, TemplateRequestHandlerTests); the runtime's row is locked (the fake finds a runtime only through
// the locked read).
public class FunctionRuntimeServiceTests
{
    private const string FnDev = "fn-dev", FnTwo = "fn-two";
    private const string Reason = "The runtime image could not be pulled onto every node of the cluster.";

    private static readonly FunctionClusterOptions FunctionClusters = new()
    {
        Clusters = [new FunctionClusterConfig { Id = FnDev, Name = "Function dev" }, new FunctionClusterConfig { Id = FnTwo, Name = "Two" }],
    };

    private static FunctionRuntime ARuntime(string name = "python3.15") =>
        FunctionRuntime.Create(name, $"ghcr.io/openvicloud/titan-function-runtimes/{name}@sha256:{new string('a', 64)}", "amd64",
            new DateOnly(2031, 10, 31));

    private static FunctionRuntime AvailableIn(string clusterId, FunctionRuntime? runtime = null)
    {
        runtime ??= ARuntime();
        runtime.SyncSucceededIn(clusterId, runtime.SyncIn(clusterId));
        return runtime;
    }

    private static FunctionRuntimeCluster Row(FunctionRuntime r, string clusterId) => r.Clusters.Single(c => c.ClusterId == clusterId);

    private static (SyncFunctionRuntimesCommandHandler Handler, FakeFunctionRuntimeRepository Repo, Mock<IProviderCommandQueue> Queue)
        SyncAll(params FunctionRuntime[] seed)
    {
        var repo = new FakeFunctionRuntimeRepository(seed);
        var queue = new Mock<IProviderCommandQueue>();
        queue.SetReturnsDefault(Task.CompletedTask);
        return (new SyncFunctionRuntimesCommandHandler(repo, FunctionClusters, queue.Object,
            NullLogger<SyncFunctionRuntimesCommandHandler>.Instance), repo, queue);
    }

    private static void VerifySyncs(Mock<IProviderCommandQueue> queue, Times times) =>
        queue.Verify(q => q.EnqueueFunctionRuntimeSyncAsync(It.IsAny<FunctionRuntime>(), It.IsAny<string>(), It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), times);

    // ── sync-all ────────────────────────────────────────────────

    [Fact]
    public async Task SyncAll_AsksEveryRuntime_IntoEveryFunctionCluster_WithOneCommandEach_KeyedOnItsRequest()
    {
        FunctionRuntime r = ARuntime();
        var (handler, repo, queue) = SyncAll(r);

        await handler.HandleAsync(new SyncFunctionRuntimesCommand());

        foreach (string clusterId in new[] { FnDev, FnTwo })
        {
            Assert.Equal(FunctionRuntimeClusterStatus.Syncing, Row(r, clusterId).Status);
            queue.Verify(q => q.EnqueueFunctionRuntimeSyncAsync(r, clusterId, Row(r, clusterId).Operation.PendingId!.Value,
                It.IsAny<CancellationToken>()), Times.Once);
        }
        Assert.True(repo.Saved);
    }

    [Fact] // a runtime there, or whose sync waits, gets nothing; a Failed or NotFound one is asked again
    public async Task SyncAll_AsksOnlyWhereNothingHoldsOrWaits()
    {
        FunctionRuntime there = AvailableIn(FnTwo, AvailableIn(FnDev, ARuntime("go")));
        FunctionRuntime waiting = ARuntime("nodejs26");
        waiting.SyncIn(FnDev);
        FunctionRuntime failed = ARuntime("python3.15");
        failed.SyncFailedIn(FnDev, failed.SyncIn(FnDev), Reason, "raw");
        AvailableIn(FnTwo, failed);
        failed.MarkNotFoundIn(FnTwo);
        var (handler, _, queue) = SyncAll(there, waiting, failed);

        await handler.HandleAsync(new SyncFunctionRuntimesCommand());

        VerifySyncs(queue, Times.Exactly(3)); // waiting: fn-two · failed: fn-dev (Failed) and fn-two (NotFound)
        queue.Verify(q => q.EnqueueFunctionRuntimeSyncAsync(there, It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        queue.Verify(q => q.EnqueueFunctionRuntimeSyncAsync(waiting, FnDev, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(FunctionRuntimeClusterStatus.Syncing, Row(failed, FnDev).Status);
        Assert.Equal(FunctionRuntimeClusterStatus.Syncing, Row(failed, FnTwo).Status);
    }

    // The repeat (titan-ops architecture/resource-lifecycle.md#repeat): a second press while the syncs wait asks for nothing.
    [Fact]
    public async Task SyncAll_Repeated_KeepsTheSameRequest_AndSendsNothingMore()
    {
        FunctionRuntime r = ARuntime();
        var (handler, _, queue) = SyncAll(r);
        await handler.HandleAsync(new SyncFunctionRuntimesCommand());
        Guid first = Row(r, FnDev).Operation.PendingId!.Value;

        await handler.HandleAsync(new SyncFunctionRuntimesCommand());

        Assert.Equal(first, Row(r, FnDev).Operation.PendingId);
        VerifySyncs(queue, Times.Exactly(2)); // the first run's two clusters
    }

    // Two sync-alls at once take turns on each runtime's row, in id order, so two of them never wait on each other.
    [Fact]
    public async Task SyncAll_LocksEachRuntimeItAsks_InTheOrderOfTheirIds_AndNoneWithNothingToAsk()
    {
        FunctionRuntime a = ARuntime("a"), b = ARuntime("b"), c = ARuntime("c");
        FunctionRuntime everywhere = AvailableIn(FnTwo, AvailableIn(FnDev, ARuntime("d")));
        var (handler, repo, _) = SyncAll(c, everywhere, a, b);

        await handler.HandleAsync(new SyncFunctionRuntimesCommand());

        Assert.Equal(new[] { a.Id, b.Id, c.Id }.OrderBy(id => id), repo.Locked);
    }

    // The syncs and their commands commit together (titan-ops architecture/provider-and-worker.md#provider-command-table).
    [Fact]
    public async Task SyncAll_EnqueuesEveryCommand_BeforeItSaves()
    {
        FunctionRuntime r = ARuntime();
        var (handler, repo, queue) = SyncAll(r);
        var savedAtEnqueue = new List<bool>();
        queue.Setup(q => q.EnqueueFunctionRuntimeSyncAsync(It.IsAny<FunctionRuntime>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => savedAtEnqueue.Add(repo.Saved))
            .Returns(Task.CompletedTask);

        await handler.HandleAsync(new SyncFunctionRuntimesCommand());

        Assert.Equal([false, false], savedAtEnqueue);
        Assert.True(repo.Saved);
    }

    [Fact] // a request saved without its command would wait for good: a waiting cluster is sent nothing again
    public async Task SyncAll_WhenAnEnqueueThrows_FailsWhole_AndSavesNothing()
    {
        FunctionRuntime r = ARuntime();
        var (handler, repo, queue) = SyncAll(r);
        queue.Setup(q => q.EnqueueFunctionRuntimeSyncAsync(r, FnTwo, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("command queue unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(new SyncFunctionRuntimesCommand()));

        Assert.False(repo.Saved);
    }

    [Fact] // prd configures no function cluster: sync-all asks nothing and adds no row
    public async Task SyncAll_WithNoFunctionCluster_AsksNothing()
    {
        FunctionRuntime r = ARuntime();
        var repo = new FakeFunctionRuntimeRepository(r);
        var queue = new Mock<IProviderCommandQueue>();
        var handler = new SyncFunctionRuntimesCommandHandler(repo, new FunctionClusterOptions(), queue.Object,
            NullLogger<SyncFunctionRuntimesCommandHandler>.Instance);

        await handler.HandleAsync(new SyncFunctionRuntimesCommand());

        Assert.Empty(r.Clusters);
        Assert.Empty(repo.Locked);
        VerifySyncs(queue, Times.Never());
    }

    // ── the report of the sync's own command ────────────────────────

    private static Task End(FakeFunctionRuntimeRepository repo, Guid runtimeId, Guid requestId, string outcome,
        string clusterId = FnDev, string? reason = null) =>
        new EndFunctionRuntimeSyncRequestCommandHandler(repo, FunctionClusters).HandleAsync(
            new EndFunctionRuntimeSyncRequestCommand(runtimeId, clusterId, requestId, outcome, reason, "raw"));

    private static Task Observe(FakeFunctionRuntimeRepository repo, Guid runtimeId, bool exists, string clusterId = FnDev) =>
        new ObserveFunctionRuntimeCommandHandler(repo, FunctionClusters).HandleAsync(
            new ObserveFunctionRuntimeCommand(runtimeId, clusterId, exists));

    [Fact]
    public async Task Done_NamingTheWaitingSync_IsAvailable_AndSaves()
    {
        FunctionRuntime r = ARuntime();
        Guid requestId = r.SyncIn(FnDev);
        var repo = new FakeFunctionRuntimeRepository(r);

        await End(repo, r.Id, requestId, "Done");

        Assert.Equal(FunctionRuntimeClusterStatus.Available, Row(r, FnDev).Status);
        Assert.True(repo.Saved);
        Assert.Equal([r.Id], repo.Locked);
    }

    [Fact]
    public async Task Failed_RecordsTheReasonAndDetail()
    {
        FunctionRuntime r = ARuntime();
        Guid requestId = r.SyncIn(FnDev);
        var repo = new FakeFunctionRuntimeRepository(r);

        await End(repo, r.Id, requestId, "Failed", reason: Reason);

        Assert.Equal(FunctionRuntimeClusterStatus.Failed, Row(r, FnDev).Status);
        Assert.Equal((Reason, "raw"), (Row(r, FnDev).Operation.FailureReason, Row(r, FnDev).Operation.FailureDetail));
        Assert.True(repo.Saved);
    }

    [Fact] // the worker has nothing to retry: 204, nothing saved
    public async Task AReport_NamingAnotherRequest_ChangesNothing_AndSavesNothing()
    {
        FunctionRuntime r = ARuntime();
        r.SyncIn(FnDev);
        var repo = new FakeFunctionRuntimeRepository(r);

        await End(repo, r.Id, Guid.NewGuid(), "Done");

        Assert.Equal(FunctionRuntimeClusterStatus.Syncing, Row(r, FnDev).Status);
        Assert.False(repo.Saved);
    }

    [Fact] // parsed before anything is loaded
    public async Task AnUnknownOutcome_IsABadRequest_AndLoadsNothing()
    {
        FunctionRuntime r = ARuntime();
        Guid requestId = r.SyncIn(FnDev);
        var repo = new FakeFunctionRuntimeRepository(r);

        var e = await Assert.ThrowsAsync<BadRequestException>(() => End(repo, r.Id, requestId, "Maybe"));

        Assert.Equal("INVALID_STATUS", e.Code);
        Assert.Empty(repo.Locked);
    }

    [Theory]
    [InlineData("fn-unknown")]
    [InlineData("singapore01")] // an Incus cluster holds no function runtime
    public async Task AReportOrObservation_ForAClusterThatIsNoFunctionCluster_IsClusterNotFound(string clusterId)
    {
        FunctionRuntime r = ARuntime();
        var repo = new FakeFunctionRuntimeRepository(r);

        var ended = await Assert.ThrowsAsync<NotFoundException>(() => End(repo, r.Id, Guid.NewGuid(), "Done", clusterId));
        var observed = await Assert.ThrowsAsync<NotFoundException>(() => Observe(repo, r.Id, true, clusterId));

        Assert.Equal(("CLUSTER_NOT_FOUND", "CLUSTER_NOT_FOUND"), (ended.Code, observed.Code));
        Assert.Empty(r.Clusters);
        Assert.False(repo.Saved);
    }

    [Fact]
    public async Task AReportOrObservation_ForNoRuntime_IsFunctionRuntimeNotFound()
    {
        var repo = new FakeFunctionRuntimeRepository();

        var ended = await Assert.ThrowsAsync<NotFoundException>(() => End(repo, Guid.NewGuid(), Guid.NewGuid(), "Done"));
        var observed = await Assert.ThrowsAsync<NotFoundException>(() => Observe(repo, Guid.NewGuid(), false));

        Assert.Equal(("FUNCTION_RUNTIME_NOT_FOUND", "FUNCTION_RUNTIME_NOT_FOUND"), (ended.Code, observed.Code));
    }

    // ── observation ────────────────────────────────────────────

    [Fact]
    public async Task Observed_NotThere_IsNotFound_AndThere_IsAvailableAgain()
    {
        FunctionRuntime r = AvailableIn(FnDev);
        var repo = new FakeFunctionRuntimeRepository(r);

        await Observe(repo, r.Id, exists: false);
        Assert.Equal(FunctionRuntimeClusterStatus.NotFound, Row(r, FnDev).Status);

        await Observe(repo, r.Id, exists: true);
        Assert.Equal(FunctionRuntimeClusterStatus.Available, Row(r, FnDev).Status);
        Assert.True(repo.Saved);
        Assert.Equal([r.Id, r.Id], repo.Locked);
    }

    [Fact]
    public async Task Observed_WhileASyncWaits_EndsNothing()
    {
        FunctionRuntime r = ARuntime();
        Guid requestId = r.SyncIn(FnDev);
        var repo = new FakeFunctionRuntimeRepository(r);

        await Observe(repo, r.Id, exists: true);

        Assert.Equal((FunctionRuntimeClusterStatus.Syncing, (Guid?)requestId, true),
            (Row(r, FnDev).Status, Row(r, FnDev).Operation.PendingId, Row(r, FnDev).IsAvailable));
    }

    // ── reads ───────────────────────────────────────────────────

    [Fact]
    public async Task AdminList_ShowsEveryRuntime_WithEachClustersStatusAndLastFailure_SortedByCluster()
    {
        FunctionRuntime go = FunctionRuntime.Create("go", $"ghcr.io/openvicloud/titan-function-runtimes/go@sha256:{new string('b', 64)}", "amd64", null);
        go.SyncFailedIn(FnTwo, go.SyncIn(FnTwo), Reason, "raw");
        AvailableIn(FnDev, go);
        FunctionRuntime python = ARuntime("python3.15");
        var repo = new FakeFunctionRuntimeRepository(python, go);

        List<AdminFunctionRuntimeQueryResult> list = await new ListAdminFunctionRuntimesQueryHandler(repo)
            .HandleAsync(new ListAdminFunctionRuntimesQuery());

        Assert.Equal(["go", "python3.15"], list.Select(r => r.Name));
        AdminFunctionRuntimeQueryResult first = list[0];
        Assert.Equal((go.Id, go.Image, "amd64", true, (DateOnly?)null),
            (first.Id, first.Image, first.Architecture, first.Listed, first.SupportEndsOn));
        Assert.Equal([FnDev, FnTwo], first.Clusters.Select(c => c.ClusterId));
        Assert.Equal(("Available", (string?)null), (first.Clusters[0].Status, first.Clusters[0].RequestFailureReason));
        Assert.Equal(("Failed", Reason, "raw"), (first.Clusters[1].Status, first.Clusters[1].RequestFailureReason, first.Clusters[1].RequestFailureDetail));
        Assert.NotNull(first.Clusters[1].RequestFailedAt);
        Assert.Equal(new DateOnly(2031, 10, 31), list[1].SupportEndsOn);
        Assert.Empty(list[1].Clusters);
    }

    [Fact]
    public async Task ReconcilerList_ShowsTheRuntimesOfThatCluster_WithTheImage_AndWhetherTitanRecordedItThere()
    {
        FunctionRuntime there = AvailableIn(FnDev, ARuntime("a"));
        FunctionRuntime waitingSeen = ARuntime("b");
        waitingSeen.SyncIn(FnDev);
        waitingSeen.MarkObservedIn(FnDev);
        FunctionRuntime failed = ARuntime("c");
        failed.SyncFailedIn(FnDev, failed.SyncIn(FnDev), Reason, "raw");
        FunctionRuntime elsewhere = AvailableIn(FnTwo, ARuntime("d"));
        var repo = new FakeFunctionRuntimeRepository(there, waitingSeen, failed, elsewhere);

        List<ReconcilableFunctionRuntimeQueryResult> rows = await new ListReconcilableFunctionRuntimesQueryHandler(repo)
            .HandleAsync(new ListReconcilableFunctionRuntimesQuery(FnDev));

        Assert.Equal(
            [(there.Id, there.Image, "Exists"), (waitingSeen.Id, waitingSeen.Image, "Exists"), (failed.Id, failed.Image, "NotFound")],
            rows.OrderBy(r => r.Image, StringComparer.Ordinal).Select(r => (r.Id, r.Image, r.CurrentExistence)));
    }
}
