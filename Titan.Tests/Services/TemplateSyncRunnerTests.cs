using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Services.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

// Counts per (template, cluster) over the two configured clusters (titan-ops features/catalog/design.md#template-per-cluster).
public class TemplateSyncRunnerTests
{
    private const string Mock = "mock", Singapore = "singapore01";

    private static readonly ClusterOptions Clusters = new()
    {
        Clusters = [new ClusterConfig { Id = Mock, Name = "Mock" }, new ClusterConfig { Id = Singapore, Name = "Singapore 01" }],
    };

    private readonly Mock<IVmTemplateRepository> _repo = new();

    // The runner injects SyncTemplatesCommandHandler (concrete, no interface — titan-ops architecture/code-structure.md#no-mediatr), so
    // the enqueue step runs for real. It reads templates through its OWN repository mock: sharing
    // _repo would let the enqueue consume the first value of a poll sequence set up below, and the
    // tests here are about polling. The enqueue itself is pinned in TemplateServiceTests.
    private readonly Mock<IVmTemplateRepository> _syncRepo = new();
    private readonly Mock<IProviderCommandQueue> _commands = new();

    public TemplateSyncRunnerTests()
    {
        _syncRepo.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        _commands.SetReturnsDefault(Task.CompletedTask);
    }

    private TemplateSyncRunner Runner() =>
        new(new SyncTemplatesCommandHandler(_syncRepo.Object, Clusters, _commands.Object,
                NullLogger<SyncTemplatesCommandHandler>.Instance),
            _repo.Object, Clusters, NullLogger<TemplateSyncRunner>.Instance);

    // A runner that skipped the enqueue and only polled would otherwise stay green.
    [Fact]
    public async Task Run_EnqueuesEveryTemplate_BeforePolling()
    {
        var t = VmTemplate.Create("a", null, "img", 10, "s");
        _syncRepo.Setup(r => r.ListAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync([t]);
        _repo.Setup(r => r.ListAllAsync(default)).ReturnsAsync([Template("a", "Exists", "Exists")]);

        await Runner().RunAsync(1, TimeSpan.Zero, CountingDelay(() => { }));

        foreach (string clusterId in new[] { Mock, Singapore })
        {
            _commands.Verify(w => w.EnqueueTemplateSyncAsync(t.Id, clusterId, "a", null, "img", null, 10,
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    [Fact]
    public async Task AllSyncedOnFirstPoll_ReturnsAllSynced_WithoutWaiting()
    {
        _repo.Setup(r => r.ListAllAsync(default))
            .ReturnsAsync([Template("a", "Exists", "Exists"), Template("b", "Exists", "Exists")]);
        var delays = 0;

        var res = await Runner().RunAsync(5, TimeSpan.Zero, CountingDelay(() => delays++));

        Assert.Equal(TemplateSyncOutcome.AllSynced, res.Outcome);
        Assert.Equal((4, 4), (res.Total, res.Synced)); // 2 templates × 2 clusters
        Assert.Equal(0, delays); // already done — never sleeps
    }

    [Fact]
    public async Task PendingThenSynced_WaitsOnceThenSucceeds()
    {
        _repo.SetupSequence(r => r.ListAllAsync(default))
            .ReturnsAsync([Template("a", "Exists", "Waiting")])
            .ReturnsAsync([Template("a", "Exists", "Exists")]);
        var delays = 0;

        var res = await Runner().RunAsync(5, TimeSpan.Zero, CountingDelay(() => delays++));

        Assert.Equal(TemplateSyncOutcome.AllSynced, res.Outcome);
        Assert.Equal(1, delays);
    }

    [Fact]
    public async Task AlwaysPending_TimesOutAfterMaxAttempts()
    {
        _repo.Setup(r => r.ListAllAsync(default)).ReturnsAsync([Template("a", "Exists", "Waiting")]);
        var delays = 0;

        var res = await Runner().RunAsync(3, TimeSpan.Zero, CountingDelay(() => delays++));

        Assert.Equal(TemplateSyncOutcome.TimedOut, res.Outcome);
        Assert.Equal(1, res.Pending);
        Assert.Equal(2, delays); // polls 3 times, waits between the first two
    }

    [Fact] // a configured cluster the template has no row in has not been synced there: it is not "done"
    public async Task AClusterWithNoRowYet_CountsPending_NotSynced()
    {
        _repo.Setup(r => r.ListAllAsync(default)).ReturnsAsync([Template("a", "Exists", "none")]);

        var res = await Runner().RunAsync(1, TimeSpan.Zero, CountingDelay(() => { }));

        Assert.Equal(TemplateSyncOutcome.TimedOut, res.Outcome);
        Assert.Equal((2, 1, 1), (res.Total, res.Synced, res.Pending));
    }

    [Fact]
    public async Task AnyFailed_ShortCircuits_WithoutWaiting()
    {
        // A Failed template won't self-heal within this run — surface it immediately
        // instead of burning the whole timeout.
        _repo.Setup(r => r.ListAllAsync(default))
            .ReturnsAsync([Template("a", "Exists", "Exists"), Template("b", "Failed", "Waiting")]);
        var delays = 0;

        var res = await Runner().RunAsync(5, TimeSpan.Zero, CountingDelay(() => delays++));

        Assert.Equal(TemplateSyncOutcome.HasFailures, res.Outcome);
        Assert.Equal(1, res.Failed);
        Assert.Equal(0, delays);
    }

    [Fact] // a cluster asked again after a give-up is waiting, not failed: the reason stays until the worker answers
    public async Task AClusterRetryingAfterAGiveUp_CountsPending_NotFailed()
    {
        _repo.Setup(r => r.ListAllAsync(default)).ReturnsAsync([Template("a", "Exists", "Retrying")]);

        var res = await Runner().RunAsync(1, TimeSpan.Zero, CountingDelay(() => { }));

        Assert.Equal((2, 1, 0, 1), (res.Total, res.Synced, res.Failed, res.Pending));
        Assert.Equal(TemplateSyncOutcome.TimedOut, res.Outcome);
    }

    [Fact] // D-5: the verdict covers the whole catalog — one failed cluster fails the run though the other synced
    public async Task OneClusterFailed_IsHasFailures_EvenWhenTheOtherClusterSynced()
    {
        _repo.Setup(r => r.ListAllAsync(default)).ReturnsAsync([Template("a", "Exists", "Failed")]);

        var res = await Runner().RunAsync(5, TimeSpan.Zero, CountingDelay(() => { }));

        Assert.Equal(TemplateSyncOutcome.HasFailures, res.Outcome);
        Assert.Equal((2, 1, 1), (res.Total, res.Synced, res.Failed));
    }

    // A run with no cluster configured syncs nothing and has nothing left pending: counted as done it would report a
    // catalog no customer can use as AllSynced. The deploy Job once ran without the cluster env exactly like that.
    [Fact]
    public async Task NoClusterConfigured_IsNoClusters_NotAllSynced()
    {
        _repo.Setup(r => r.ListAllAsync(default)).ReturnsAsync([Template("a", "none", "none")]);
        var runner = new TemplateSyncRunner(
            new SyncTemplatesCommandHandler(_syncRepo.Object, new ClusterOptions(), _commands.Object,
                NullLogger<SyncTemplatesCommandHandler>.Instance),
            _repo.Object, new ClusterOptions(), NullLogger<TemplateSyncRunner>.Instance);

        var res = await runner.RunAsync(5, TimeSpan.Zero, CountingDelay(() => { }));

        Assert.Equal(TemplateSyncOutcome.NoClusters, res.Outcome);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Func<TimeSpan, CancellationToken, Task> CountingDelay(Action onDelay) =>
        (_, _) => { onDelay(); return Task.CompletedTask; };

    // A template's sync in mock and in singapore01: "none" (never asked) | "Waiting" | "Exists" | "Failed" |
    // "Retrying" (failed, then asked again).
    private static VmTemplate Template(string name, string inMock, string inSingapore)
    {
        var t = VmTemplate.Create(name, null, "os-image", 10, "m");
        Put(t, Mock, inMock);
        Put(t, Singapore, inSingapore);
        return t;
    }

    private static void Put(VmTemplate t, string clusterId, string state)
    {
        if (state == "none")
        {
            return;
        }
        t.RequestSyncIn(clusterId);
        if (state == "Exists")
        {
            t.MarkCreatedIn(clusterId, $"ext-{t.Name}-{clusterId}");
        }
        if (state == "Failed")
        {
            t.MarkRequestFailedIn(clusterId, "provider unreachable", null);
        }
        if (state == "Retrying")
        {
            t.MarkRequestFailedIn(clusterId, "provider unreachable", null);
            t.RequestSyncIn(clusterId);
        }
    }
}
