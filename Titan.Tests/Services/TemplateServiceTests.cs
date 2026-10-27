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
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Values;

namespace Titan.Tests.Services;

public class TemplateServiceTests
{
    // The tier each template points at. Sizing no longer travels with a sync, so these are only ids.
    private const string TierS = "s", TierM = "m", TierL = "l";

    // Two clusters, so every assertion about one cluster can be contradicted by the other
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-cluster).
    private const string Mock = "mock", Singapore = "singapore01";

    private static readonly ClusterOptions Clusters = new()
    {
        Clusters = [new ClusterConfig { Id = Mock, Name = "Mock" }, new ClusterConfig { Id = Singapore, Name = "Singapore 01" }],
    };

    private static (TemplateFacade svc, StubRepo repo, Mock<IProviderCommandQueue> wf) Make(params VmTemplate[] seed)
    {
        var repo = new StubRepo(seed);
        var wf = new Mock<IProviderCommandQueue>();
        wf.SetReturnsDefault(Task.CompletedTask);
        var syncLog = new SyncCountLogger();
        var svc = new TemplateFacade(
            new ListTemplatesQueryHandler(repo, Clusters),
            new ListAdminTemplatesQueryHandler(repo),
            new UpdateTemplateStatusCommandHandler(repo, Clusters, NullLogger<UpdateTemplateStatusCommandHandler>.Instance),
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
        UpdateTemplateStatusCommandHandler updateStatus,
        SyncTemplatesCommandHandler sync,
        SyncCountLogger syncLog)
    {
        public Task<List<TemplateQueryResult>> ListAsync(string? clusterId = null, CancellationToken ct = default)
            => list.HandleAsync(new ListTemplatesQuery(clusterId), ct);

        public Task<List<AdminTemplateQueryResult>> ListAllForAdminAsync(CancellationToken ct = default)
            => listAdmin.HandleAsync(new ListAdminTemplatesQuery(), ct);

        public Task UpdateStatusAsync(Guid templateId, UpdateTemplateStatusRequest req, CancellationToken ct = default)
            => updateStatus.HandleAsync(
                new UpdateTemplateStatusCommand(templateId, req.ClusterId!, req.Report, req.Exists, req.ExternalId, req.Reason,
                    req.Detail),
                ct);

        public async Task<(int Enqueued, int Failed)> SyncAllAsync(CancellationToken ct = default)
        {
            await sync.HandleAsync(new SyncTemplatesCommand(), ct);
            return syncLog.Counts ?? throw new Xunit.Sdk.XunitException("the sync handler logged no summary line");
        }
    }

    // Captures the one summary line SyncTemplatesCommandHandler writes. Reads the structured values,
    // not the rendered text, so a wording change in the message does not break the tests.
    private sealed class SyncCountLogger : ILogger<SyncTemplatesCommandHandler>
    {
        public (int Enqueued, int Failed)? Counts { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values) return;
            var map = values.ToDictionary(kv => kv.Key, kv => kv.Value);
            if (map.TryGetValue("Enqueued", out var e) && map.TryGetValue("Total", out _) && map.TryGetValue("Failed", out var f))
                Counts = ((int)e!, (int)f!);
        }
    }

    // Asked into mock, nothing observed yet.
    private static VmTemplate PendingTemplate()
    {
        var t = VmTemplate.Create("Ubuntu-Web", "web", "Ubuntu-20.04", 20, TierS);
        t.RequestSyncIn(Mock);
        return t;
    }

    private static VmTemplate SyncedTemplate(string name, string extId, string clusterId = Mock)
    {
        var t = VmTemplate.Create(name, "d", "img", 10, TierS);
        t.RequestSyncIn(clusterId);
        t.MarkCreatedIn(clusterId, extId);
        return t;
    }

    private static VmTemplate FailedTemplate(string name, string reason, string clusterId = Mock)
    {
        var t = VmTemplate.Create(name, null, "img", 10, TierS);
        t.RequestSyncIn(clusterId);
        t.MarkRequestFailedIn(clusterId, reason, null);
        return t;
    }

    private static UpdateTemplateStatusRequest Report(string report, bool? exists = null, string? extId = null,
        string? reason = null, string? detail = null, string clusterId = Mock) =>
        new(report, exists, extId, reason, detail, clusterId);

    private static string Shape(VmTemplate t, string clusterId)
    {
        VmTemplateCluster? cluster = t.ClusterOrNull(clusterId);
        if (cluster == null)
        {
            return "none";
        }
        string transition = "-";
        if (cluster.Transition != null)
        {
            transition = cluster.Transition.ToString()!;
        }
        return $"{cluster.CurrentExistence}/{cluster.DesiredExistence}/{transition}";
    }

    private static void VerifySync(Mock<IProviderCommandQueue> wf, Times times) =>
        wf.Verify(w => w.EnqueueTemplateSyncAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), times);

    // ── ListAsync (customer read) ───────────────────────────────────

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-listed: the customer catalog leaves out templates not offered, even
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
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-is-an-image-alias).
    [Fact]
    public async Task SyncAll_PassesTheTemplatesImageUrl()
    {
        const string url = "https://images.example.test/ubuntu-{arch}.img";
        var t = TemplateSeedFields.WithImageUrl(VmTemplate.Create("a", null, "img", 10, TierM), url);
        var (svc, _, wf) = Make(t);

        await svc.SyncAllAsync();

        wf.Verify(w => w.EnqueueTemplateSyncAsync(t.Id, Mock, "a", null, "img", url, 10,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlySyncedTemplates_MappedToDto_HidingInternalFields()
    {
        var pending = PendingTemplate();
        var synced = VmTemplate.Create("CentOS", "db", "CentOS-7.9", 40, TierL);
        synced.RequestSyncIn(Mock);
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
        // and no Cpu/Vcpu/MemoryMb either (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-borrows-sizing): the
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

    [Fact] // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#cluster-is-a-real-choice — an empty list would read "nothing synced there"
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
        Assert.Equal(("NotCreated", "Syncing", (string?)null), (dtos["Ubuntu-Web"].CurrentExistence, dtos["Ubuntu-Web"].Transition, dtos["Ubuntu-Web"].RequestFailureReason));
        Assert.Equal(("Exists", (string?)null, (string?)null), (dtos["CentOS"].CurrentExistence, dtos["CentOS"].Transition, dtos["CentOS"].RequestFailureReason));
        Assert.Equal(("NotCreated", (string?)null, (string?)"boom"), (dtos["Broken"].CurrentExistence, dtos["Broken"].Transition, dtos["Broken"].RequestFailureReason));
    }

    [Fact] // the raw provider error is the operator's: only the admin list carries it (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure)
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
    public async Task ListAllForAdmin_Exists_ExposesExternalTemplateId()
    {
        var (svc, _, _) = Make(SyncedTemplate("Ubuntu", "205"));

        var dto = Assert.Single(await svc.ListAllForAdminAsync());

        var cluster = Assert.Single(dto.Clusters);
        Assert.Equal((Mock, "Exists", "Exists", "205"), (cluster.ClusterId, cluster.CurrentExistence, cluster.DesiredExistence, cluster.ExternalTemplateId));
        Assert.Equal("Ubuntu", dto.Name);
        Assert.Equal("img", dto.Os);
    }

    [Fact] // one template, two clusters: two existences, two ids, ordered by cluster id
    public async Task ListAllForAdmin_ShowsEachClustersExistenceAndId()
    {
        var t = SyncedTemplate("Ubuntu", "205", Singapore);
        t.RequestSyncIn(Mock);
        var (svc, _, _) = Make(t);

        var clusters = Assert.Single(await svc.ListAllForAdminAsync()).Clusters;

        Assert.Equal(
            [(Mock, "NotCreated", (string?)"Syncing", (string?)null), (Singapore, "Exists", null, "205")],
            clusters.Select(z => (z.ClusterId, z.CurrentExistence, z.Transition, z.ExternalTemplateId)));
    }

    [Fact] // an alias seen gone keeps its id: the admin sees which alias to look for
    public async Task ListAllForAdmin_AnAliasSeenGone_IsNotFound_WithItsId()
    {
        var t = SyncedTemplate("Ubuntu", "tpl-1");
        t.MarkNotFoundIn(Mock);
        var (svc, _, _) = Make(t);

        var cluster = Assert.Single(Assert.Single(await svc.ListAllForAdminAsync()).Clusters);

        Assert.Equal(("NotFound", "tpl-1"), (cluster.CurrentExistence, cluster.ExternalTemplateId));
    }

    [Fact]
    public async Task ListAllForAdmin_WhenEmpty_ReturnsEmptyList_Not404()
    {
        var (svc, _, _) = Make();

        Assert.Empty(await svc.ListAllForAdminAsync());
    }

    // ── UpdateStatus (worker callback) ─────────────────────────────────────

    [Fact] // the sync command: the alias is there and of the cluster's architecture
    public async Task UpdateStatus_Created_RecordsTheAlias_AndTheTemplateExists()
    {
        var t = PendingTemplate();
        var (svc, repo, _) = Make(t);

        await svc.UpdateStatusAsync(t.Id, Report("Created", extId: "tpl-205"));

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Equal("tpl-205", t.ClusterOrNull(Mock)!.ExternalTemplateId);
        Assert.True(repo.Saved);
        Assert.Single(await svc.ListAsync(Mock));
    }

    [Fact] // a report names its cluster; the other cluster is not touched by it
    public async Task UpdateStatus_InOneCluster_LeavesTheOtherClusterAlone()
    {
        var t = PendingTemplate();
        t.RequestSyncIn(Singapore);
        var (svc, _, _) = Make(t);

        await svc.UpdateStatusAsync(t.Id, Report("Created", extId: "3", clusterId: Singapore));

        Assert.Equal("Exists/Exists/-", Shape(t, Singapore));
        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
    }

    [Fact] // a worker report for a cluster Titan does not have must not invent a row for it
    public async Task UpdateStatus_UnknownCluster_ThrowsClusterNotFound_AndRecordsNothing()
    {
        var t = PendingTemplate();
        var (svc, repo, _) = Make(t);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.UpdateStatusAsync(t.Id, Report("Created", extId: "9", clusterId: "vn-hcm-1")));

        Assert.Equal("CLUSTER_NOT_FOUND", ex.Code);
        Assert.Null(t.ClusterOrNull("vn-hcm-1"));
        Assert.False(repo.Saved);
    }

    [Fact]
    public async Task UpdateStatus_CreatedWithoutExternalId_Rejected_SoWeNeverRecordATemplateWeCannotReference()
    {
        var t = PendingTemplate();
        var (svc, repo, _) = Make(t);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            svc.UpdateStatusAsync(t.Id, Report("Created")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal("NotCreated/Exists/Syncing", Shape(t, Mock));
        Assert.False(repo.Saved);
    }

    [Fact] // the sync command gave up (wrong architecture, no cluster): the reason for the operator, existence unchanged
    public async Task UpdateStatus_RequestFailed_RecordsReasonAndDetail_AndEndsTheWait()
    {
        var t = PendingTemplate();
        var (svc, _, _) = Make(t);

        await svc.UpdateStatusAsync(t.Id, Report("RequestFailed", reason: "Template image has the wrong architecture.", detail: "x86_64 on aarch64"));

        Assert.Equal("NotCreated/Exists/-", Shape(t, Mock));
        Assert.Equal("Template image has the wrong architecture.", t.ClusterOrNull(Mock)!.RequestFailureReason);
        Assert.Equal("x86_64 on aarch64", t.ClusterOrNull(Mock)!.RequestFailureDetail);
        Assert.Empty(await svc.ListAsync(Mock));
    }

    // The template reconciler reports what the cluster holds.
    [Fact]
    public async Task UpdateStatus_ObservedNotThere_RecordsNotFound_AndHidesItFromCustomers()
    {
        var t = SyncedTemplate("Ubuntu", "205");
        var (svc, repo, _) = Make(t);

        await svc.UpdateStatusAsync(t.Id, Report("Observed", exists: false));

        Assert.Equal("NotFound/Exists/-", Shape(t, Mock));
        Assert.Equal("205", t.ClusterOrNull(Mock)!.ExternalTemplateId);
        Assert.True(repo.Saved);
        Assert.Empty(await svc.ListAsync(Mock));
    }

    [Fact]
    public async Task UpdateStatus_ObservedThere_OnALostTemplate_RecordsItExistsAgain()
    {
        var t = SyncedTemplate("Ubuntu", "205");
        t.MarkNotFoundIn(Mock);
        var (svc, _, _) = Make(t);

        await svc.UpdateStatusAsync(t.Id, Report("Observed", exists: true));

        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.Single(await svc.ListAsync(Mock));
    }

    [Fact]
    public async Task UpdateStatus_ObservedWithoutExists_Rejected()
    {
        var t = SyncedTemplate("Ubuntu", "205");
        var (svc, repo, _) = Make(t);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            svc.UpdateStatusAsync(t.Id, Report("Observed")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal("Exists/Exists/-", Shape(t, Mock));
        Assert.False(repo.Saved);
    }

    [Theory] // the words of the old body are refused, not read as something else
    [InlineData("Bogus")]
    [InlineData("Synced")]
    [InlineData("Absent")]
    [InlineData("Deleted")]
    public async Task UpdateStatus_UnknownReport_Rejected(string report)
    {
        var t = PendingTemplate();
        var (svc, repo, _) = Make(t);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            svc.UpdateStatusAsync(t.Id, Report(report, extId: "1")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.False(repo.Saved);
    }

    [Fact] // a malformed report is the worker's bug, whatever the template: refused before anything is looked up
    public async Task UpdateStatus_MalformedReport_OnAnUnknownTemplate_Is400_Not404()
    {
        var (svc, _, _) = Make();

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            svc.UpdateStatusAsync(Guid.NewGuid(), Report("Created")));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task UpdateStatus_UnknownTemplate_ThrowsNotFound()
    {
        var (svc, _, _) = Make();

        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            svc.UpdateStatusAsync(Guid.NewGuid(), Report("Created", extId: "1")));

        Assert.Equal("TEMPLATE_NOT_FOUND", ex.Code);
    }

    // ── SyncAll (admin trigger) ─────────────────────────────────────

    [Fact] // every template into every cluster, whatever its state there
    public async Task SyncAll_EnqueuesEveryTemplate_RegardlessOfStatus()
    {
        var pending = VmTemplate.Create("a-pending", null, "img", 10, TierS);
        var synced = SyncedTemplate("b-synced", "50");
        var failed = FailedTemplate("c-failed", "boom");
        var (svc, _, wf) = Make(pending, synced, failed);

        var result = await svc.SyncAllAsync();

        Assert.Equal(6, result.Enqueued); // 3 templates × 2 clusters
        Assert.Equal(0, result.Failed);
        VerifySync(wf, Times.Exactly(6));
    }

    [Fact] // one command per cluster, each carrying its cluster — the worker resolves that cluster from it
    public async Task SyncAll_SendsEachClusterItsOwnCommand()
    {
        var t = VmTemplate.Create("a", null, "img", 10, TierM);
        var (svc, _, wf) = Make(t);

        await svc.SyncAllAsync();

        foreach (string clusterId in new[] { Mock, Singapore })
        {
            wf.Verify(w => w.EnqueueTemplateSyncAsync(t.Id, clusterId, "a", null, "img", null, 10,
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    // A cluster the template is not on yet gets a sync REQUEST before it is enqueued.
    [Fact]
    public async Task SyncAll_RequestsASync_ForEveryTemplateNotYetSynced_AndForNoOther()
    {
        var failed = FailedTemplate("a-failed", "boom");
        var synced = SyncedTemplate("b-synced", "50");
        var (svc, repo, _) = Make(failed, synced);

        await svc.SyncAllAsync();

        Assert.Equal("NotCreated/Exists/Syncing", Shape(failed, Mock));
        Assert.Equal("Exists/Exists/-", Shape(synced, Mock));
        Assert.True(repo.Saved);
    }

    // The sync requests and their commands must commit together (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-table):
    // every enqueue happens before the one save, so no command is left staged after it.
    [Fact]
    public async Task SyncAll_EnqueuesEveryCommand_BeforeItSaves()
    {
        var t = VmTemplate.Create("a", null, "img", 10, TierS);
        var (svc, repo, wf) = Make(t);
        List<bool> savedAtEnqueue = new();
        wf.Setup(w => w.EnqueueTemplateSyncAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
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

        Assert.Equal("Exists/Exists/-", Shape(synced, Mock));
        Assert.Equal("NotCreated/Exists/Syncing", Shape(synced, Singapore));
        Assert.Equal("NotCreated/Exists/Syncing", Shape(fresh, Mock));
        Assert.Equal("NotCreated/Exists/Syncing", Shape(fresh, Singapore));
    }

    // The repeat (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat): a second press while a sync
    // waits asks for nothing new — the same request stays — and only enqueues the command again.
    [Fact]
    public async Task SyncAll_Repeated_KeepsTheSameRequest_AndResendsTheCommand()
    {
        var t = FailedTemplate("a", "boom");
        var (svc, _, wf) = Make(t);
        await svc.SyncAllAsync();
        Guid? firstRequest = t.ClusterOrNull(Mock)!.PendingIntentId;

        await svc.SyncAllAsync();

        Assert.NotNull(firstRequest);
        Assert.Equal(firstRequest, t.ClusterOrNull(Mock)!.PendingIntentId);
        VerifySync(wf, Times.Exactly(4)); // two runs × two clusters
    }

    [Fact]
    public async Task SyncAll_WhenNoTemplates_EnqueuesNothing()
    {
        var (svc, _, wf) = Make();

        var result = await svc.SyncAllAsync();

        Assert.Equal(0, result.Enqueued);
        Assert.Equal(0, result.Failed);
        VerifySync(wf, Times.Never());
    }

    [Fact] // one cluster's enqueue failing costs neither the other templates nor the other cluster theirs (D-5)
    public async Task SyncAll_WhenOneEnqueueThrows_CountsFailed_AndContinuesWithTheRest()
    {
        var a = VmTemplate.Create("a", null, "img", 10, TierS);
        var b = VmTemplate.Create("b", null, "img", 10, TierS);
        var (svc, _, wf) = Make(a, b);
        wf.Setup(w => w.EnqueueTemplateSyncAsync(a.Id, Singapore, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("command queue unavailable"));

        var result = await svc.SyncAllAsync();

        Assert.Equal(3, result.Enqueued);
        Assert.Equal(1, result.Failed);
        VerifySync(wf, Times.Exactly(4)); // all four were attempted
    }

    private sealed class StubRepo(params VmTemplate[] seed) : IVmTemplateRepository
    {
        private readonly List<VmTemplate> _items = [.. seed];
        public bool Saved { get; private set; }

        public Task<VmTemplate?> FindByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_items.FirstOrDefault(t => t.Id == id));

        // Deliberately unsorted (insertion order): ordering is the SERVICE's job, so the
        // ordering test fails if the service stops sorting.
        public Task<List<VmTemplate>> ListSyncedInClusterAsync(string clusterId, CancellationToken ct = default) =>
            Task.FromResult(_items.Where(t => t.IsSyncedIn(clusterId)).ToList());

        public Task<List<VmTemplate>> ListSyncedInAnyClusterAsync(CancellationToken ct = default) =>
            Task.FromResult(_items.Where(t => t.Clusters.Any(z => z.CurrentExistence == CurrentExistence.Exists)).ToList());

        public Task<List<VmTemplate>> ListReconcilableInClusterAsync(string clusterId, CancellationToken ct = default) =>
            Task.FromResult(_items.Where(t => t.ClusterOrNull(clusterId) != null && t.ClusterOrNull(clusterId)!.ExternalTemplateId != null).ToList());

        public Task<List<VmTemplate>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult(_items.OrderBy(t => t.Name).ToList());

        public void Add(VmTemplate template) => _items.Add(template);

        public Task SaveAsync(CancellationToken ct = default) { Saved = true; return Task.CompletedTask; }
    }
}
