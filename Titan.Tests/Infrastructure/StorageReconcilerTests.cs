using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.Volume;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Aggregates.VolumeSnapshot;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the storage reconciler: GET /internal/reconcile/volumes returns the volumes and
// volume snapshots of a cluster, with the existence and attachment Titan last recorded, and every call is the
// reconciler's heartbeat. The repository predicates are in VolumeRepositoryStateTests.
public class StorageReconcilerTests
{
    private const string Cluster = "vn-hcm-1";

    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"storage-reconcile-{Guid.NewGuid()}")
            .Options);

    private static ListReconcilableStorageEndpoint Endpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string token) =>
        Factory.Create<ListReconcilableStorageEndpoint>(
            ctx =>
            {
                ctx.Request.Headers["X-Internal-Token"] = token;
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={Cluster}");
            },
            new ListReconcilableStorageQueryHandler(new VolumeRepository(db), new VolumeSnapshotRepository(db), new VmRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    [Fact]
    public async Task Endpoint_WithToken_ReturnsVolumesAndSnapshots_WithWhatWasLastSeen_AndRecordsTheHeartbeat()
    {
        using var db = NewDb();
        var volume = Volume.CreateUnchecked(Guid.NewGuid(), "data", Cluster, "vo-std", 20);
        volume.MarkCreated("img-7");
        volume.MarkObserved(null, true);
        var snapshot = VolumeSnapshot.CreateUnchecked(volume.Id, volume.TenantId, "snap");
        snapshot.MarkCreated("img-8");
        db.Volumes.Add(volume);
        db.VolumeSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = Endpoint(db, heartbeat, "secret");

        await ep.HandleAsync(CancellationToken.None);

        var v = Assert.Single(ep.Response.Volumes);
        Assert.Equal(volume.Id, v.Id);
        Assert.Equal("img-7", v.ExternalId);
        Assert.Equal("Exists", v.CurrentExistence);
        Assert.Null(v.CurrentAttachedExternalVmId);
        Assert.True(v.CurrentAttachedElsewhere);
        var s = Assert.Single(ep.Response.Snapshots);
        Assert.Equal(snapshot.Id, s.Id);
        Assert.Equal(volume.Id, s.VolumeId);
        Assert.Equal("img-8", s.ExternalId);
        Assert.Equal("Exists", s.CurrentExistence);
        Assert.NotNull(heartbeat.Snapshot()[$"volumes/{Cluster}"].LastPollAt);
    }

    // The worker compares the instance names it reads in used_by, so the list names the attached VM by its instance.
    [Fact]
    public async Task Endpoint_NamesTheAttachedVm_ByItsIncusInstance()
    {
        using var db = NewDb();
        var vm = Titan.Domain.Aggregates.Vm.Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl-1", Cluster, 20);
        vm.MarkCreated("vm0123456789abcdefghijklmnop");
        var volume = Volume.CreateUnchecked(vm.TenantId, "data", Cluster, "vo-std", 20);
        volume.MarkCreated("img-7");
        volume.MarkObserved(vm.Id, false);
        db.Vms.Add(vm);
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();
        var ep = Endpoint(db, new ReconcilerHeartbeat(), "secret");

        await ep.HandleAsync(CancellationToken.None);

        var v = Assert.Single(ep.Response.Volumes);
        Assert.Equal("vm0123456789abcdefghijklmnop", v.CurrentAttachedExternalVmId);
        Assert.False(v.CurrentAttachedElsewhere);
    }

    [Fact]
    public async Task Endpoint_WrongToken_Returns401_AndLeavesNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = Endpoint(db, heartbeat, "wrong");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Empty(heartbeat.Snapshot());
    }
}
