using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the VM pool reconciler: GET /internal/reconcile/vms returns one cluster's provisioned VMs
// with the state and addresses Titan last recorded, and every call is the reconciler's heartbeat.
public class VmReconcilerTests
{
    private const string Cluster = "vn-hcm-1";

    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"vm-reconcile-{Guid.NewGuid()}")
            .Options);

    private static Vm VmIn(string cluster, string name) =>
        Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), name, "m", "tpl-1", cluster, 20);

    private static ListReconcilableVmsEndpoint Endpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string token) =>
        Factory.Create<ListReconcilableVmsEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = token;
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={Cluster}");
            },
            new ListReconcilableVmsQueryHandler(new VmRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    // A VM in Error or not found stays in the list: Incus may still hold it, and a VM that recovers there must read
    // Running again. A deleted VM and one without a provider id are out.
    [Fact]
    public async Task List_HoldsTheClustersProvisionedVms_IncludingErrorAndNotFound_ButNotDeleted()
    {
        using var db = NewDb();
        Vm creating = VmIn(Cluster, "creating");
        creating.MarkCreated("40");
        Vm running = VmIn(Cluster, "running");
        running.MarkRunning("41", "10.0.0.5");
        Vm failed = VmIn(Cluster, "failed");
        failed.MarkRunning("42", "10.0.0.6");
        failed.MarkObserved(VmStatus.Error, null);
        Vm lost = VmIn(Cluster, "lost");
        lost.MarkRunning("46", "10.0.0.9");
        lost.MarkNotFound();
        Vm destroyed = VmIn(Cluster, "destroyed");
        destroyed.MarkRunning("43", "10.0.0.7");
        destroyed.Delete(Guid.NewGuid());
        destroyed.MarkDeleted();
        Vm withoutId = VmIn(Cluster, "without-id");
        Vm otherCluster = VmIn("vn-han-1", "other-cluster");
        otherCluster.MarkRunning("44", "10.0.0.8");
        db.Vms.AddRange(creating, running, failed, lost, destroyed, withoutId, otherCluster);
        await db.SaveChangesAsync();

        List<Vm> listed = await new VmRepository(db).ListReconcilableAsync(Cluster);

        Assert.Equal(new[] { "creating", "failed", "lost", "running" }, listed.Select(v => v.Name).OrderBy(n => n).ToArray());
    }

    // A create that gave up after the provider had the instance IS observed: Incus holds an instance, and one that runs
    // is billed whether or not its create ended well (titan-ops architecture/resource-lifecycle.md#reconciler, Tôn Chủ
    // 2026-11-02). Its delete reaches the provider through the delete command.
    [Fact]
    public async Task List_IncludesACreateThatGaveUp_AfterTheProviderIdArrived()
    {
        using var db = NewDb();
        Vm refused = VmIn(Cluster, "gave-up");
        db.Vms.Add(refused);
        await db.SaveChangesAsync();
        var handler = new EndVmRequestCommandHandler(new VmRepository(db), new VolumeRepository(db), new FakeActivityRecorder());
        await handler.HandleAsync(new EndVmRequestCommand(refused.Id, refused.Operation.PendingId!.Value, "Failed", ExternalId: "45",
            Reason: "The VM could not be created."));

        List<Vm> listed = await new VmRepository(db).ListReconcilableAsync(Cluster);

        Assert.Equal("The VM could not be created.", refused.Operation.FailureReason);
        Assert.Equal("45", refused.ExternalId);
        Assert.Contains(listed, v => v.Id == refused.Id);
    }

    [Fact]
    public async Task Endpoint_WithToken_ReturnsWhatTitanLastRecorded_AndRecordsTheHeartbeat()
    {
        using var db = NewDb();
        Vm vm = VmIn(Cluster, "vm-1");
        vm.MarkRunning("41", "10.0.0.5", "2001:db8::5");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat(db);
        ListReconcilableVmsEndpoint ep = Endpoint(db, heartbeat, "secret");

        await ep.HandleAsync(CancellationToken.None);

        var row = Assert.Single(ep.Response);
        Assert.Equal(vm.Id, row.Id);
        Assert.Equal("41", row.ExternalId);
        Assert.Equal("Running", row.CurrentState);
        Assert.Equal("Exists", row.CurrentExistence);
        Assert.Equal("10.0.0.5", row.IpAddress);
        Assert.Equal("2001:db8::5", row.PublicIpv6Address);
        Assert.True((await heartbeat.SnapshotAsync(CancellationToken.None)).ContainsKey($"vms/{Cluster}"));
    }

    // The worker contract keeps CurrentExistence, now read off the status and the Incus status
    // (titan-ops features/vm/design.md#vm-status): NotFound stops the worker repeating an absent instance, Exists with a
    // status stops it repeating a status Titan already has, and anything else makes it report next cycle.
    [Theory]
    [InlineData("not-found", "NotFound", null)]
    [InlineData("running", "Exists", "Running")]
    [InlineData("created-without-a-word", "NotCreated", null)]
    [InlineData("delete-waiting-not-found", "NotCreated", null)]
    public void Row_GivesCurrentExistence_FromTheStatusAndTheIncusWord(string history, string existence, string? status)
    {
        Vm vm = VmIn(Cluster, "vm-1");
        if (history == "created-without-a-word")
        {
            vm.MarkCreated("41");
        }
        else
        {
            vm.MarkRunning("41", "10.0.0.5");
        }
        if (history == "not-found")
        {
            vm.MarkNotFound();
            Assert.Equal(VmStatus.NotFound, vm.Status);
        }
        if (history == "delete-waiting-not-found")
        {
            vm.Delete(Guid.NewGuid());
            vm.MarkNotFound();
            Assert.Equal(VmStatus.Deleting, vm.Status);
        }

        ReconcilableVmQueryResult row = ReconcilableVmQueryResult.From(vm);

        Assert.Equal(existence, row.CurrentExistence);
        Assert.Equal(status, row.CurrentState);
        Assert.Equal("41", row.ExternalId);
    }

    [Fact]
    public async Task Endpoint_WrongToken_Returns401_AndLeavesNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat(db);
        ListReconcilableVmsEndpoint ep = Endpoint(db, heartbeat, "wrong");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Empty((await heartbeat.SnapshotAsync(CancellationToken.None)));
    }
}
