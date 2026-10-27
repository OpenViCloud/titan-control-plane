using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The volume queries that decide quota and billing, one row per situation a volume can be in.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it).
public class VolumeRepositoryStateTests
{
    private static readonly Guid Tenant = Guid.NewGuid();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"volume-state-{Guid.NewGuid()}")
            .Options);

    private static Volume Creating(string name) =>
        Volume.CreateUnchecked(Tenant, name, "vn-hcm-1", "vo-std", 20);

    private static Volume Available(string name)
    {
        var volume = Creating(name);
        volume.MarkCreated($"ext-{name}");
        return volume;
    }

    private static Vm RunningVm()
    {
        var vm = Vm.CreateUnchecked(Tenant, Guid.NewGuid(), "vm-1", "m", "tpl-1", "vn-hcm-1", 20);
        vm.MarkRunning("ext-vm", "10.0.0.5");
        return vm;
    }

    private static Volume AttachRequested(string name, Vm vm)
    {
        var volume = Available(name);
        volume.Attach(vm);
        return volume;
    }

    private static Volume Attached(string name, Vm vm)
    {
        var volume = AttachRequested(name, vm);
        volume.MarkObserved(vm.Id, false);
        return volume;
    }

    private static Volume DetachRequested(string name)
    {
        var volume = Attached(name, RunningVm());
        volume.Detach();
        return volume;
    }

    private static Volume DeleteRequested(string name)
    {
        var volume = Available(name);
        volume.RequestDelete(liveSnapshotCount: 0);
        return volume;
    }

    private static Volume NotFound(string name)
    {
        var volume = Available(name);
        volume.MarkNotFound();
        return volume;
    }

    private static Volume CreateFailed(string name)
    {
        var volume = Creating(name);
        volume.MarkRequestFailed("could not create", "raw");
        return volume;
    }

    private static Volume Deleted(string name)
    {
        var volume = DeleteRequested(name);
        volume.MarkDeleted();
        return volume;
    }

    // Occupancy (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#counting-and-billing): a volume whose delete was pressed stops counting
    // at once, so the customer can create its replacement; one Incus no longer has, or never had, does not count.
    [Fact]
    public async Task Quota_CountsWhatIncusHoldsOrIsAboutTo_ButNotADeleteInFlight_NotFound_NorAFailedCreate()
    {
        using var db = NewDb();
        db.Volumes.AddRange(
            Creating("new"), Available("ready"), Attached("attached", RunningVm()), DeleteRequested("leaving"),
            NotFound("lost"), CreateFailed("failed"), Deleted("gone"));
        await db.SaveChangesAsync();

        int count = await new VolumeRepository(db).CountActiveByTenantAsync(Tenant);

        Assert.Equal(3, count);
    }

    // Q-C: billing follows what Incus holds, a delete in flight included, until Incus has deleted it.
    [Fact]
    public async Task Billing_IsWhatIncusHolds_ADeleteInFlightIncluded()
    {
        using var db = NewDb();
        db.Volumes.AddRange(
            Creating("creating"), Available("available"), AttachRequested("attaching", RunningVm()),
            Attached("attached", RunningVm()), DetachRequested("detaching"), DeleteRequested("deleting"),
            NotFound("lost"), CreateFailed("failed"), Deleted("deleted"));
        await db.SaveChangesAsync();

        var billed = (await new VolumeRepository(db).ListBillableAsync()).Select(v => v.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "attached", "attaching", "available", "deleting", "detaching" }, billed);
    }

    [Fact]
    public async Task Reconcilable_IsWhatReachedIncusInTheCluster_AndIsNotConfirmedGone()
    {
        using var db = NewDb();
        var elsewhere = Volume.CreateUnchecked(Tenant, "elsewhere", "vn-han-1", "vo-std", 20);
        elsewhere.MarkCreated("ext-elsewhere");
        db.Volumes.AddRange(
            Creating("creating"), Available("available"), Attached("attached", RunningVm()), DeleteRequested("deleting"),
            Deleted("deleted"), NotFound("lost"), CreateFailed("failed"), elsewhere);
        await db.SaveChangesAsync();

        var names = (await new VolumeRepository(db).ListReconcilableAsync("vn-hcm-1")).Select(v => v.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "attached", "available", "deleting", "lost" }, names);
    }

    // ── Volume snapshots ───────────────────────────────────────────────────

    private static VolumeSnapshot SnapshotOf(Volume volume, string name, bool created = true)
    {
        var snapshot = VolumeSnapshot.CreateUnchecked(volume.Id, Tenant, name);
        if (created)
        {
            snapshot.MarkCreated($"ext-{name}");
        }
        return snapshot;
    }

    // One count serves the per-volume quota and VOLUME_HAS_SNAPSHOTS (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#counting-and-billing).
    [Fact]
    public async Task SnapshotQuota_CountsCreatingAndExisting_ButNotADeleteInFlight_NotFound_NorAFailedCreate()
    {
        using var db = NewDb();
        var volume = Available("vol");
        var leaving = SnapshotOf(volume, "leaving");
        leaving.RequestDelete();
        var lost = SnapshotOf(volume, "lost");
        lost.MarkNotFound();
        var failed = SnapshotOf(volume, "failed", created: false);
        failed.MarkRequestFailed("could not create", "raw");
        db.Volumes.Add(volume);
        db.VolumeSnapshots.AddRange(SnapshotOf(volume, "creating", created: false), SnapshotOf(volume, "ready"), leaving, lost, failed);
        await db.SaveChangesAsync();

        int count = await new VolumeSnapshotRepository(db).CountActiveByVolumeIdAsync(volume.Id);

        Assert.Equal(2, count);
    }

    // Q-C: a snapshot whose delete is in flight still bills until Incus has deleted it.
    [Fact]
    public async Task SnapshotBilling_IsWhatIncusHolds_ADeleteInFlightIncluded()
    {
        using var db = NewDb();
        var volume = Available("vol");
        var leaving = SnapshotOf(volume, "leaving");
        leaving.RequestDelete();
        var gone = SnapshotOf(volume, "gone");
        gone.RequestDelete();
        gone.MarkDeleted();
        var lost = SnapshotOf(volume, "lost");
        lost.MarkNotFound();
        db.Volumes.Add(volume);
        db.VolumeSnapshots.AddRange(SnapshotOf(volume, "creating", created: false), SnapshotOf(volume, "ready"), leaving, gone, lost);
        await db.SaveChangesAsync();

        var billed = (await new VolumeSnapshotRepository(db).ListBillableAsync()).Select(s => s.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "leaving", "ready" }, billed);
    }

    [Fact] // a snapshot has no cluster; it lives where its volume lives
    public async Task SnapshotReconcilable_FollowsItsVolumesCluster()
    {
        using var db = NewDb();
        var here = Available("here");
        var there = Volume.CreateUnchecked(Tenant, "there", "vn-han-1", "vo-std", 20);
        there.MarkCreated("ext-there");
        var gone = SnapshotOf(here, "gone");
        gone.RequestDelete();
        gone.MarkDeleted();
        db.Volumes.AddRange(here, there);
        db.VolumeSnapshots.AddRange(SnapshotOf(here, "mine"), SnapshotOf(here, "creating", created: false), gone, SnapshotOf(there, "theirs"));
        await db.SaveChangesAsync();

        var names = (await new VolumeSnapshotRepository(db).ListReconcilableAsync("vn-hcm-1")).Select(s => s.Name).ToList();

        Assert.Equal(new[] { "mine" }, names);
    }

    // The volume's Deleted report marks these: Incus deleted them with the volume.
    [Fact]
    public async Task SnapshotsLiveOfVolume_AreEveryOneNotYetDeleted_OfThatVolumeOnly()
    {
        using var db = NewDb();
        var volume = Available("vol");
        var other = Available("other");
        var leaving = SnapshotOf(volume, "leaving");
        leaving.RequestDelete();
        var lost = SnapshotOf(volume, "lost");
        lost.MarkNotFound();
        var gone = SnapshotOf(volume, "gone");
        gone.RequestDelete();
        gone.MarkDeleted();
        db.Volumes.AddRange(volume, other);
        db.VolumeSnapshots.AddRange(SnapshotOf(volume, "ready"), leaving, lost, gone, SnapshotOf(other, "theirs"));
        await db.SaveChangesAsync();

        var names = (await new VolumeSnapshotRepository(db).ListLiveOfVolumeForUpdateAsync(volume.Id))
            .Select(s => s.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "leaving", "lost", "ready" }, names);
    }

    // The volumes a VM's delete detaches (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/volume/design.md#vm-delete-releases-volumes): the ones Incus
    // shows this VM using or that were asked to be attached to it, and never a deleted one or another VM's.
    [Fact]
    public async Task ListAttachedToVm_ReturnsThisVmsLiveVolumes_Only()
    {
        using var db = NewDb();
        Vm vm = RunningVm();
        Vm otherVm = RunningVm();
        Volume attached = Attached("attached", vm);
        Volume attaching = AttachRequested("attaching", vm);
        Volume attachedOutsideTitan = Available("outside");
        attachedOutsideTitan.MarkObserved(vm.Id, false);
        Volume otherVms = AttachRequested("other", otherVm);
        Volume deleted = Available("deleted");
        deleted.MarkObserved(vm.Id, false);
        deleted.MarkDeleted();
        Volume loose = Available("loose");
        db.Volumes.AddRange(attached, attaching, attachedOutsideTitan, otherVms, deleted, loose);
        await db.SaveChangesAsync();

        List<Volume> found = await new VolumeRepository(db).ListAttachedToVmAsync(vm.Id);

        Assert.Equal(new[] { "attached", "attaching", "outside" }, found.Select(v => v.Name).OrderBy(n => n).ToArray());
    }
}
