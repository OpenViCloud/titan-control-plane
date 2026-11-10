using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Tests.Fakes;
using Titan.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Titan.Application.Options;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Application.Queries.Catalog.VmTemplate.Contracts;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

public class TemplateServiceTests
{
    // The tier each template points at. Sizing no longer travels with a sync, so these are only ids.
    private const string TierS = "s", TierM = "m", TierL = "l";

    // Two clusters, so every assertion about one cluster can be contradicted by the other
    // (titan-ops features/catalog/design.md#template-per-cluster).
    private const string Mock = "mock", Singapore = "singapore01";

    private static readonly ClusterOptions Clusters = new()
    {
        Clusters = [new ClusterConfig { Id = Mock, Name = "Mock" }, new ClusterConfig { Id = Singapore, Name = "Singapore 01" }],
    };

    private static (TemplateFacade svc, FakeVmTemplateRepository repo, Mock<IProviderCommandQueue> wf) Make(params VmTemplate[] seed)
    {
        var repo = new FakeVmTemplateRepository(seed);
        var wf = new Mock<IProviderCommandQueue>();
        wf.SetReturnsDefault(Task.CompletedTask);
        var syncLog = new SyncCountLogger();
        var svc = new TemplateFacade(
            new ListTemplatesQueryHandler(repo, Clusters),
            new ListAdminTemplatesQueryHandler(repo),
            new EndTemplateSyncRequestCommandHandler(repo, Clusters),
            new ObserveTemplateCommandHandler(repo, Clusters),
            new SyncTemplatesCommandHandler(repo, Clusters, wf.Object, syncLog),
            syncLog);
        return (svc, repo, wf);
    }

    // Facade over the template handlers so the tests keep one call shape.
    //
    // SyncTemplates returns nothing (POST .../sync answers 202 with no body); the handler logs the
    // per-run counts for the operator, so the facade reads them back from that log line and the count
    // assertions below still pin the counting behaviour.
    private sealed class TemplateFacade(
        ListTemplatesQueryHandler list,
        ListAdminTemplatesQueryHandler listAdmin,
        EndTemplateSyncRequestCommandHandler endSync,
        ObserveTemplateCommandHandler observe,
        SyncTemplatesCommandHandler sync,
        SyncCountLogger syncLog)
    {
        public Task<List<TemplateQueryResult>> ListAsync(string? clusterId = null, CancellationToken ct = default)
            => list.HandleAsync(new ListTemplatesQuery(clusterId), ct);

        public Task<List<AdminTemplateQueryResult>> ListAllForAdminAsync(CancellationToken ct = default)
            => listAdmin.HandleAsync(new ListAdminTemplatesQuery(), ct);

        public Task EndSyncAsync(Guid templateId, Guid requestId, string outcome, string? externalId = null,
            string? reason = null, string? detail = null, CancellationToken ct = default)
            => endSync.HandleAsync(
                new EndTemplateSyncRequestCommand(templateId, Mock, requestId, outcome, externalId, reason, detail), ct);

        public Task ObserveAsync(Guid templateId, bool exists, CancellationToken ct = default)
            => observe.HandleAsync(new ObserveTemplateCommand(templateId, Mock, exists), ct);

        public async Task<int> SyncAllAsync(CancellationToken ct = default)
        {
            await sync.HandleAsync(new SyncTemplatesCommand(), ct);
            return syncLog.Counts ?? throw new Xunit.Sdk.XunitException("the sync handler logged no summary line");
        }
    }

    // Captures the one summary line SyncTemplatesCommandHandler writes. Reads the structured values,
    // not the rendered text, so a wording change in the message does not break the tests.
    private sealed class SyncCountLogger : ILogger<SyncTemplatesCommandHandler>
    {
        public int? Counts { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return;
            var map = values.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (map.TryGetValue("Enqueued", out var e) && map.TryGetValue("Total", out _))
                Counts = (int)e!;
        }
    }

    // Asked into mock, nothing observed yet.
    private static VmTemplate PendingTemplate()
    {
        var t = VmTemplate.Create("Ubuntu-Web", "web", "Ubuntu-20.04", 20, TierS);
        t.SyncIn(Mock);
        return t;
    }

    private static VmTemplate SyncedTemplate(string name, string extId, string clusterId = Mock)
    {
        var t = VmTemplate.Create(name, "d", "img", 10, TierS);
        t.SyncIn(clusterId);
        t.MarkCreatedIn(clusterId, extId);
        return t;
    }

    private static VmTemplate FailedTemplate(string name, string reason, string clusterId = Mock)
    {
        var t = VmTemplate.Create(name, null, "img", 10, TierS);
        t.SyncIn(clusterId);
        t.MarkRequestFailedIn(clusterId, reason, null);
        return t;
    }

    private static Guid WaitingSync(VmTemplate t) => t.Clusters.Single(c => c.ClusterId == Mock).Operation.PendingId!.Value;

    private static string Shape(VmTemplate t, string clusterId)
    {
        VmTemplateCluster? cluster = t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == clusterId);
        if (cluster == null)
        {
            return "none";
        }
        return cluster.Status.ToString();
    }

    private static void VerifySync(Mock<IProviderCommandQueue> wf, Times times) =>
        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.IsAny<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), times);

    private static Guid WaitingSyncOf(VmTemplate t, string clusterId) =>
        t.Clusters.Single(candidate => candidate.ClusterId == clusterId).Operation.PendingId!.Value;

    // ── Templates never asked into a cluster (titan-ops features/catalog/design.md#template-sync-never-asked) ──

    private static Task RunNeverAsked(FakeVmTemplateRepository repo, Mock<IProviderCommandQueue> wf) =>
        Titan.Infrastructure.Hosting.TemplateSyncNeverAskedService.RunOnceAsync(
            new ListClustersWithNeverAskedTemplatesQueryHandler(repo, Clusters),
            new SyncTemplatesCommandHandler(repo, Clusters, wf.Object, new SyncCountLogger()),
            NullLogger.Instance, CancellationToken.None);

    // A new environment (empty database, the catalog from the migrations) or a cluster added to the config: nobody
    // presses sync, so the control plane asks itself.
    [Fact]
    public async Task NeverAsked_AClusterATemplateWasNeverAskedInto_AsksEveryTemplateThere()
    {
        var t = SyncedTemplate("in-mock-only", "5", Mock);
        var (_, repo, wf) = Make(t);

        await RunNeverAsked(repo, wf);

        Assert.Equal("Syncing", Shape(t, Singapore));
        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a38 => a38.Id == t.Id), Singapore, WaitingSyncOf(t, Singapore), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(repo.Saved);
    }

    // Asked once is enough: a failed sync waits for the admin, as before, instead of being retried every tick.
    [Fact]
    public async Task NeverAsked_EveryTemplateAskedInEveryCluster_SendsNothing_EvenWhenOneFailed()
    {
        var t = FailedTemplate("failed-in-mock", "bad image", Mock);
        t.SyncIn(Singapore);
        t.MarkCreatedIn(Singapore, "9");
        var (_, repo, wf) = Make(t);

        await RunNeverAsked(repo, wf);

        VerifySync(wf, Times.Never());
        Assert.False(repo.Saved);
    }

    // One template is enough: a template added later by a migration is asked although the others are everywhere.
    [Fact]
    public async Task NeverAsked_OneTemplateMissingACluster_AmongOthersThatAreThere_IsAsked()
    {
        var everywhere = SyncedTemplate("everywhere", "1", Mock);
        everywhere.SyncIn(Singapore);
        everywhere.MarkCreatedIn(Singapore, "2");
        var added = SyncedTemplate("added-later", "3", Mock);
        var (_, repo, wf) = Make(everywhere, added);

        await RunNeverAsked(repo, wf);

        Assert.Equal("Syncing", Shape(added, Singapore));
    }

    [Fact]
    public async Task NeverAsked_AnEmptyCatalog_SendsNothing()
    {
        var (_, repo, wf) = Make();

        await RunNeverAsked(repo, wf);

        VerifySync(wf, Times.Never());
    }

    // ── ListAsync (customer read) ───────────────────────────────────

    // titan-ops features/catalog/design.md#template-listed: the customer catalog leaves out templates not offered, even
    // synced ones; admin still sees them, flagged.
    [Theory]
    [InlineData(Mock)]
    [InlineData(null)]
    public async Task ListAsync_LeavesOutUnlistedTemplates_EvenWhenSynced(string? clusterId)
    {
        var offered = SyncedTemplate("Ubuntu", "70");
        var hidden = TemplateSeedFields.Unlisted(SyncedTemplate("Alma", "71"));
        var (svc, _, _) = Make(offered, hidden);

        var dtos = await svc.ListAsync(clusterId);

        Assert.Equal(["Ubuntu"], dtos.Select(d => d.Name));
    }

    [Fact]
    public async Task AdminList_ShowsUnlistedTemplates_Flagged()
    {
        var offered = SyncedTemplate("Ubuntu", "70");
        var hidden = TemplateSeedFields.Unlisted(SyncedTemplate("Alma", "71"));
        var (svc, _, _) = Make(offered, hidden);

        var dtos = await svc.ListAllForAdminAsync();

        Assert.True(dtos.Single(d => d.Name == "Ubuntu").Listed);
        Assert.False(dtos.Single(d => d.Name == "Alma").Listed);
    }

    // The worker imports the distro's own image when the template names one
    // (titan-ops features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task SyncAll_PassesTheTemplatesImageUrl()
    {
        const string url = "https://images.example.test/ubuntu-{arch}.img";
        var t = TemplateSeedFields.WithImageUrl(VmTemplate.Create("a", null, "img", 10, TierM), url);
        var (svc, _, wf) = Make(t);

        await svc.SyncAllAsync();

        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a39 => a39.Id == t.Id && a39.Name == "a" && a39.OsImageName == "img" && a39.ImageUrl == url), Mock, WaitingSyncOf(t, Mock), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlySyncedTemplates_MappedToDto_HidingInternalFields()
    {
        var pending = PendingTemplate();
        var synced = VmTemplate.Create("CentOS", "db", "CentOS-7.9", 40, TierL);
        synced.SyncIn(Mock);
        synced.MarkCreatedIn(Mock, "77");
        var failed = FailedTemplate("Broken", "boom");
        var (svc, _, _) = Make(pending, synced, failed);

        var dtos = await svc.ListAsync(Mock);

        // Only the Synced one surfaces; Pending/Failed never reach the customer.
        var dto = Assert.Single(dtos);
        Assert.Equal("CentOS", dto.Name);
        Assert.Equal("CentOS-7.9", dto.Os);   // Os == OsImageName
        Assert.Equal(40, dto.DiskGb);
        // TemplateQueryResult has no Status/ExternalTemplateId/StatusReason members — masking is structural —
        // and no Cpu/Vcpu/MemoryMb either (titan-ops features/catalog/design.md#template-borrows-sizing): the
        // template's tier is a base for the provider, not a description of the customer's VM. Naming those
        // members here would not compile.
    }

    [Fact] // a template synced in mock only does not exist for a customer creating in singapore01
    public async Task ListAsync_InACluster_ShowsOnlyTemplatesSyncedInThatCluster()
    {
        var inMock = SyncedTemplate("in-mock", "5", Mock);
        var inSingapore = SyncedTemplate("in-singapore", "6", Singapore);
        var (svc, _, _) = Make(inMock, inSingapore);

        Assert.Equal(["in-mock"], (await svc.ListAsync(Mock)).Select(d => d.Name));
        Assert.Equal(["in-singapore"], (await svc.ListAsync(Singapore)).Select(d => d.Name));
    }

    [Fact] // no cluster: names the template of a VM in any cluster (VM list page)
    public async Task ListAsync_WithoutACluster_ShowsTemplatesSyncedInAnyCluster()
    {
        var inMock = SyncedTemplate("in-mock", "5", Mock);
        var inSingapore = SyncedTemplate("in-singapore", "6", Singapore);
        var nowhere = PendingTemplate();
        var (svc, _, _) = Make(inSingapore, inMock, nowhere);

        Assert.Equal(["in-mock", "in-singapore"], (await svc.ListAsync()).Select(d => d.Name));
    }

    [Fact] // titan-ops features/catalog/design.md#cluster-is-a-real-choice — an empty list would read "nothing synced there"
    public async Task ListAsync_UnknownCluster_ThrowsClusterNotFound()
    {
        var (svc, _, _) = Make(SyncedTemplate("a", "1"));

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => svc.ListAsync("vn-hcm-1"));

        Assert.Equal("CLUSTER_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task ListAsync_WhenNoSyncedTemplates_ReturnsEmptyList_Not404()
    {
        var (svc, _, _) = Make(PendingTemplate()); // only Pending

        var dtos = await svc.ListAsync(Mock);

        Assert.Empty(dtos);
    }

    [Fact]
    public async Task ListAsync_OrdersByNameAscending_RegardlessOfInsertionOrder()
    {
        // Seeded deliberately out of name order — a stable catalog listing is part of the
        // customer contract, and the stub repo returns insertion order.
        var (svc, _, _) = Make(
            SyncedTemplate("Ubuntu-20.04-Web", "1"),
            SyncedTemplate("CentOS-7-DB", "2"),
            SyncedTemplate("Debian-11-Minimal", "3"));

        var dtos = await svc.ListAsync(Mock);

        Assert.Equal(
            new[] { "CentOS-7-DB", "Debian-11-Minimal", "Ubuntu-20.04-Web" },
            dtos.Select(d => d.Name));
    }

    // ── ListAllForAdminAsync (admin list-all-status) ──────────────

    [Fact]
    public async Task ListAllForAdmin_ReturnsEveryClusterRow_NotJustTheOnesThatExist()
    {
        var pending = PendingTemplate();
        var synced = SyncedTemplate("CentOS", "77");
        var failed = FailedTemplate("Broken", "boom");
        var (svc, _, _) = Make(pending, synced, failed);

        var dtos = (await svc.ListAllForAdminAsync()).ToDictionary(d => d.Name, d => d.Clusters.Single());

        // Unlike the customer ListAsync, admin sees a waiting and a failed sync too.
        Assert.Equal(("Syncing", (string?)null), (dtos["Ubuntu-Web"].Status, dtos["Ubuntu-Web"].RequestFailureReason));
        Assert.Equal(("Available", (string?)null), (dtos["CentOS"].Status, dtos["CentOS"].RequestFailureReason));
        Assert.Equal(("Failed", (string?)"boom"), (dtos["Broken"].Status, dtos["Broken"].RequestFailureReason));
    }

    // The admin JSON carries the one status and none of the three fields it replaced (titan-ops features/catalog/design.md#template-per-cluster).
    [Fact]
    public async Task ListAllForAdmin_Json_CarriesStatus_AndNoExistenceOrTransition()
    {
        var syncing = PendingTemplate();
        var (svc, _, _) = Make(syncing, SyncedTemplate("CentOS", "77"));

        var dtos = await svc.ListAllForAdminAsync();
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            dtos.Single(d => d.Name == "Ubuntu-Web").Clusters.Single(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var names = json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(
            ["clusterId", "externalTemplateId", "requestFailedAt", "requestFailureDetail", "requestFailureReason", "status"],
            names);
        Assert.Equal("Syncing", json.RootElement.GetProperty("status").GetString());
    }

    [Fact] // the raw provider error is the operator's: only the admin list carries it (titan-ops architecture/desired-and-current.md#request-failure)
    public async Task ListAllForAdmin_AFailedSync_CarriesReasonDetailAndTime_SoAdminCanDiagnose()
    {
        var t = PendingTemplate();
        t.MarkRequestFailedIn(Mock, "Template image has the wrong architecture.", "image is x86_64, cluster is aarch64");
        var (svc, _, _) = Make(t);

        var cluster = Assert.Single(Assert.Single(await svc.ListAllForAdminAsync()).Clusters);

        Assert.Equal("Template image has the wrong architecture.", cluster.RequestFailureReason);
        Assert.Equal("image is x86_64, cluster is aarch64", cluster.RequestFailureDetail);
        Assert.NotNull(cluster.RequestFailedAt);
    }

    [Fact]
    public async Task ListAllForAdmin_Available_ExposesExternalTemplateId()
    {
        var (svc, _, _) = Make(SyncedTemplate("Ubuntu", "205"));

        var dto = Assert.Single(await svc.ListAllForAdminAsync());

        var cluster = Assert.Single(dto.Clusters);
        Assert.Equal((Mock, "Available", "205"), (cluster.ClusterId, cluster.Status, cluster.ExternalTemplateId));
        Assert.Equal("Ubuntu", dto.Name);
        Assert.Equal("img", dto.Os);
    }

    [Fact] // one template, two clusters: two statuses, two ids, ordered by cluster id
    public async Task ListAllForAdmin_ShowsEachClustersStatusAndId()
    {
        var t = SyncedTemplate("Ubuntu", "205", Singapore);
        t.SyncIn(Mock);
        var (svc, _, _) = Make(t);

        var clusters = Assert.Single(await svc.ListAllForAdminAsync()).Clusters;

        Assert.Equal(
            [(Mock, "Syncing", (string?)null), (Singapore, "Available", "205")],
            clusters.Select(z => (z.ClusterId, z.Status, z.ExternalTemplateId)));
    }

    [Fact] // an alias seen gone keeps its id: the admin sees which alias to look for
    public async Task ListAllForAdmin_AnAliasSeenGone_IsNotFound_WithItsId()
    {
        var t = SyncedTemplate("Ubuntu", "tpl-1");
        t.MarkNotFoundIn(Mock);
        var (svc, _, _) = Make(t);

        var cluster = Assert.Single(Assert.Single(await svc.ListAllForAdminAsync()).Clusters);

        Assert.Equal(("NotFound", "tpl-1"), (cluster.Status, cluster.ExternalTemplateId));
    }

    [Fact]
    public async Task ListAllForAdmin_WhenEmpty_ReturnsEmptyList_Not404()
    {
        var (svc, _, _) = Make();

        Assert.Empty(await svc.ListAllForAdminAsync());
    }

    // ── The worker's reports: what customers see ─────────────────────────

    [Fact] // the sync command: the alias is there and of the cluster's architecture
    public async Task SyncDone_RecordsTheAlias_AndTheTemplateIsAvailable()
    {
        var t = PendingTemplate();
        var (svc, repo, _) = Make(t);

        await svc.EndSyncAsync(t.Id, WaitingSync(t), "Done", "tpl-205");

        Assert.Equal("Available", Shape(t, Mock));
        Assert.Equal("tpl-205", t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.ExternalTemplateId);
        Assert.True(repo.Saved);
        Assert.Single(await svc.ListAsync(Mock));
    }

    [Fact] // the sync command gave up (wrong architecture, no cluster): the reason for the operator, and the cluster holds no alias
    public async Task SyncFailed_RecordsReasonAndDetail_AndEndsTheWait()
    {
        var t = PendingTemplate();
        var (svc, _, _) = Make(t);

        await svc.EndSyncAsync(t.Id, WaitingSync(t), "Failed", reason: "Template image has the wrong architecture.", detail: "x86_64 on aarch64");

        Assert.Equal("Failed", Shape(t, Mock));
        Assert.Equal("Template image has the wrong architecture.", t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.Operation.FailureReason);
        Assert.Equal("x86_64 on aarch64", t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.Operation.FailureDetail);
        Assert.Empty(await svc.ListAsync(Mock));
    }

    // The template reconciler reports what the cluster holds.
    [Fact]
    public async Task ObservedNotThere_RecordsNotFound_AndHidesItFromCustomers()
    {
        var t = SyncedTemplate("Ubuntu", "205");
        var (svc, repo, _) = Make(t);

        await svc.ObserveAsync(t.Id, exists: false);

        Assert.Equal("NotFound", Shape(t, Mock));
        Assert.Equal("205", t.Clusters.FirstOrDefault(candidate => candidate.ClusterId == Mock)!.ExternalTemplateId);
        Assert.True(repo.Saved);
        Assert.Empty(await svc.ListAsync(Mock));
    }

    [Fact]
    public async Task ObservedThere_OnALostTemplate_RecordsItAvailableAgain()
    {
        var t = SyncedTemplate("Ubuntu", "205");
        t.MarkNotFoundIn(Mock);
        var (svc, _, _) = Make(t);

        await svc.ObserveAsync(t.Id, exists: true);

        Assert.Equal("Available", Shape(t, Mock));
        Assert.Single(await svc.ListAsync(Mock));
    }

    // ── SyncAll (admin trigger) ─────────────────────────────────────

    // A command goes with each sync it asks, and only with those: a cluster that has the alias, or whose sync waits,
    // gets nothing (titan-ops architecture/resource-lifecycle.md#repeat).
    [Fact]
    public async Task SyncAll_SendsACommand_OnlyForTheSyncsItAsks()
    {
        var fresh = VmTemplate.Create("a-fresh", null, "img", 10, TierS);
        var synced = SyncedTemplate("b-synced", "50");
        var failed = FailedTemplate("c-failed", "boom");
        var waiting = PendingTemplate();
        var (svc, _, wf) = Make(fresh, synced, failed, waiting);

        var result = await svc.SyncAllAsync();

        // fresh: both clusters · synced: singapore01 only · failed: both · waiting: singapore01 only
        Assert.Equal(6, result);
        VerifySync(wf, Times.Exactly(6));
        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a40 => a40.Id == synced.Id), Mock, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a41 => a41.Id == waiting.Id), Mock, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact] // one command per cluster, each carrying its cluster — the worker resolves that cluster from it
    public async Task SyncAll_SendsEachClusterItsOwnCommand()
    {
        var t = VmTemplate.Create("a", null, "img", 10, TierM);
        var (svc, _, wf) = Make(t);

        await svc.SyncAllAsync();

        foreach (string clusterId in new[] { Mock, Singapore })
        {
            wf.Verify(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a42 => a42.Id == t.Id && a42.Name == "a" && a42.OsImageName == "img" && a42.ImageUrl == null), clusterId, WaitingSyncOf(t, clusterId), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    // Two sync-alls at once take turns on each template's row (titan-ops architecture/resource-lifecycle.md#one-key-per-request);
    // in the order of the ids, so two of them never wait on each other.
    [Fact]
    public async Task SyncAll_LocksEachTemplateItAsks_InTheOrderOfTheirIds()
    {
        var a = VmTemplate.Create("a", null, "img", 10, TierS);
        var b = VmTemplate.Create("b", null, "img", 10, TierS);
        var c = VmTemplate.Create("c", null, "img", 10, TierS);
        var (svc, repo, _) = Make(c, a, b);

        await svc.SyncAllAsync();

        Assert.Equal(new[] { a.Id, b.Id, c.Id }.OrderBy(id => id), repo.Locked);
    }

    // Nothing to ask of a template there in every cluster: its row is not locked.
    [Fact]
    public async Task SyncAll_LocksNoTemplate_ThatHasNothingToAsk()
    {
        var t = SyncedTemplate("everywhere", "1", Mock);
        t.SyncIn(Singapore);
        t.MarkCreatedIn(Singapore, "2");
        var (svc, repo, wf) = Make(t);

        await svc.SyncAllAsync();

        Assert.Empty(repo.Locked);
        VerifySync(wf, Times.Never());
    }

    // A cluster the template is not on yet gets a sync REQUEST before it is enqueued.
    [Fact]
    public async Task SyncAll_RequestsASync_ForEveryTemplateNotYetSynced_AndForNoOther()
    {
        var failed = FailedTemplate("a-failed", "boom");
        var synced = SyncedTemplate("b-synced", "50");
        var (svc, repo, _) = Make(failed, synced);

        await svc.SyncAllAsync();

        Assert.Equal("Syncing", Shape(failed, Mock));
        Assert.Equal("Available", Shape(synced, Mock));
        Assert.True(repo.Saved);
    }

    // The sync requests and their commands must commit together (titan-ops architecture/provider-and-worker.md#provider-command-table):
    // every enqueue happens before the one save, so no command is left staged after it.
    [Fact]
    public async Task SyncAll_EnqueuesEveryCommand_BeforeItSaves()
    {
        var t = VmTemplate.Create("a", null, "img", 10, TierS);
        var (svc, repo, wf) = Make(t);
        List<bool> savedAtEnqueue = new();
        wf.Setup(w => w.EnqueueTemplateSyncAsync(It.IsAny<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(), It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => savedAtEnqueue.Add(repo.Saved))
            .Returns(Task.CompletedTask);

        await svc.SyncAllAsync();

        Assert.Equal([false, false], savedAtEnqueue); // two clusters, both staged before the save
        Assert.True(repo.Saved);
    }

    [Fact] // a template with no row in a cluster is asked into it — which is how a new cluster gets the whole catalog
    public async Task SyncAll_RequestsASyncInEveryCluster_TheTemplateIsNotSyncedIn()
    {
        var synced = SyncedTemplate("b-synced", "50", Mock);
        var fresh = VmTemplate.Create("c-fresh", null, "img", 10, TierS);
        var (svc, _, _) = Make(synced, fresh);

        await svc.SyncAllAsync();

        Assert.Equal("Available", Shape(synced, Mock));
        Assert.Equal("Syncing", Shape(synced, Singapore));
        Assert.Equal("Syncing", Shape(fresh, Mock));
        Assert.Equal("Syncing", Shape(fresh, Singapore));
    }

    // The repeat (titan-ops architecture/resource-lifecycle.md#repeat): a second press while a sync waits asks for nothing
    // new — the same request stays — and sends nothing more.
    [Fact]
    public async Task SyncAll_Repeated_KeepsTheSameRequest_AndSendsNothingMore()
    {
        var t = FailedTemplate("a", "boom");
        var (svc, _, wf) = Make(t);
        await svc.SyncAllAsync();
        Guid firstRequest = WaitingSyncOf(t, Mock);

        var second = await svc.SyncAllAsync();

        Assert.Equal(firstRequest, WaitingSyncOf(t, Mock));
        Assert.Equal(0, second);
        VerifySync(wf, Times.Exactly(2)); // the first run's two clusters
    }

    [Fact] // the alias went missing on Incus: sync-all pulls it again, and a present one is left alone
    public async Task SyncAll_AsksAgain_ForAClusterWhoseAliasIsGone_AndNotForOneThatHasIt()
    {
        var lost = SyncedTemplate("a-lost", "50");
        lost.MarkNotFoundIn(Mock);
        var there = SyncedTemplate("b-there", "51");
        var (svc, _, wf) = Make(lost, there);

        await svc.SyncAllAsync();

        Assert.Equal("Syncing", Shape(lost, Mock));
        Assert.Equal("Available", Shape(there, Mock));
        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a44 => a44.Id == lost.Id), Mock, WaitingSyncOf(lost, Mock), It.IsAny<CancellationToken>()), Times.Once);
        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a45 => a45.Id == there.Id), Mock, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAll_WhenNoTemplates_EnqueuesNothing()
    {
        var (svc, _, wf) = Make();

        var result = await svc.SyncAllAsync();

        Assert.Equal(0, result);
        VerifySync(wf, Times.Never());
    }

    [Fact]
    public async Task SyncAll_WhenAnEnqueueThrows_FailsWhole_AndSavesNoRequest()
    {
        var a = VmTemplate.Create("a", null, "img", 10, TierS);
        var b = VmTemplate.Create("b", null, "img", 10, TierS);
        var (svc, repo, wf) = Make(a, b);
        wf.Setup(w => w.EnqueueTemplateSyncAsync(It.Is<global::Titan.Domain.Aggregates.VmTemplate.VmTemplate>(a46 => a46.Id == a.Id), Singapore, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("command queue unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SyncAllAsync());

        // A request saved without its command would wait for good: the next sync-all sends a waiting cluster nothing.
        Assert.False(repo.Saved);
    }
}
