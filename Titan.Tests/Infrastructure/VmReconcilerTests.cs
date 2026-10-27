using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the VM pool reconciler: GET /internal/reconcile/vms returns one zone's provisioned VMs
// with the state and addresses Titan last recorded, and every call is the reconciler's heartbeat.
public class VmReconcilerTests
{
    private const string Zone = "vn-hcm-1";

    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"vm-reconcile-{Guid.NewGuid()}")
            .Options);

    private static Vm VmIn(string zone, string name) =>
        Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), name, "m", "tpl-1", zone, 20);

    private static ListReconcilableVmsEndpoint Endpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string token) =>
        Factory.Create<ListReconcilableVmsEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = token;
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={Zone}");
            },
            new ListReconcilableVmsQueryHandler(new VmRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    // A VM in Error or not found stays in the list: Incus may still hold it, and a VM that recovers there must read
    // Running again. A deleted VM and one without a provider id are out.
    [Fact]
    public async Task List_HoldsTheZonesProvisionedVms_IncludingErrorAndNotFound_ButNotDeleted()
    {
        using var db = NewDb();
        Vm creating = VmIn(Zone, "creating");
        creating.MarkCreated("40");
        Vm running = VmIn(Zone, "running");
        running.MarkRunning("41", "10.0.0.5");
        Vm failed = VmIn(Zone, "failed");
        failed.MarkRunning("42", "10.0.0.6");
        failed.MarkObserved(VmState.Error, null);
        Vm lost = VmIn(Zone, "lost");
        lost.MarkRunning("46", "10.0.0.9");
        lost.MarkNotFound();
        Vm destroyed = VmIn(Zone, "destroyed");
        destroyed.MarkRunning("43", "10.0.0.7");
        destroyed.RequestDelete();
        destroyed.MarkDeleted();
        Vm withoutId = VmIn(Zone, "without-id");
        Vm otherZone = VmIn("vn-han-1", "other-zone");
        otherZone.MarkRunning("44", "10.0.0.8");
        db.Vms.AddRange(creating, running, failed, lost, destroyed, withoutId, otherZone);
        await db.SaveChangesAsync();

        List<Vm> listed = await new VmRepository(db).ListReconcilableAsync(Zone);

        Assert.Equal(new[] { "creating", "failed", "lost", "running" }, listed.Select(v => v.Name).OrderBy(n => n).ToArray());
    }

    // A create that gave up after the provider had the instance is not observed: observing it would turn a create
    // that gave up into a running VM, billed and counted (Tôn Chủ, 2026-09-30). Its delete still reaches the
    // provider through the delete command, which reports Deleted itself.
    [Fact]
    public async Task List_LeavesOutACreateThatGaveUp_AfterTheProviderIdArrived()
    {
        using var db = NewDb();
        Vm refused = VmIn(Zone, "gave-up");
        db.Vms.Add(refused);
        await db.SaveChangesAsync();
        var handler = new UpdateVmStatusCommandHandler(new VmRepository(db), new VmSnapshotRepository(db), new VolumeRepository(db), new FakeActivityRecorder());
        await handler.HandleAsync(new UpdateVmStatusCommand(refused.Id, "RequestFailed", ExternalId: "45",
            Reason: "The VM could not be created."));

        List<Vm> listed = await new VmRepository(db).ListReconcilableAsync(Zone);

        Assert.Equal("The VM could not be created.", refused.RequestFailureReason);
        Assert.Equal("45", refused.ExternalId);
        Assert.DoesNotContain(listed, v => v.Id == refused.Id);
    }

    [Fact]
    public async Task Endpoint_WithToken_ReturnsWhatTitanLastRecorded_AndRecordsTheHeartbeat()
    {
        using var db = NewDb();
        Vm vm = VmIn(Zone, "vm-1");
        vm.MarkRunning("41", "10.0.0.5", "2001:db8::5");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat();
        ListReconcilableVmsEndpoint ep = Endpoint(db, heartbeat, "secret");

        await ep.HandleAsync(CancellationToken.None);

        var row = Assert.Single(ep.Response);
        Assert.Equal(vm.Id, row.Id);
        Assert.Equal("41", row.ExternalId);
        Assert.Equal("Running", row.CurrentState);
        Assert.Equal("Exists", row.CurrentExistence);
        Assert.Equal("10.0.0.5", row.IpAddress);
        Assert.Equal("2001:db8::5", row.PublicIpv6Address);
        Assert.NotNull(heartbeat.Snapshot()[$"vms/{Zone}"].LastPollAt);
    }

    [Fact]
    public async Task Endpoint_WrongToken_Returns401_AndLeavesNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat();
        ListReconcilableVmsEndpoint ep = Endpoint(db, heartbeat, "wrong");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Empty(heartbeat.Snapshot());
    }
}
