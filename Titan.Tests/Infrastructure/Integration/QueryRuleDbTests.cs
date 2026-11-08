using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The quota and billing queries on Postgres, each the SQL twin of a plain bool on the aggregate (titan-ops
/// features/vm/design.md#counting-and-billing, features/volume/design.md#counting-and-billing,
/// features/network/design.md#ipv6-quota, features/load-balancer/design.md#lb-quota-and-billing), one row per side of
/// each condition, as fixed expectations. The repository state tests run the same queries on InMemory, which runs the
/// LINQ, not the SQL, and there every shape is checked against the plain bool on the aggregate
/// (titan-ops architecture/domain-methods.md#plain-code).
/// Category=Integration — its own CI step; needs real Postgres (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
///
/// Rows of other tests share the database, so a billing list is read for this test's rows only.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class QueryRuleDbTests(PostgresFixture pg)
{
    private readonly Tenant _tenant = TestTenant.WithRoomToSpare();

    private async Task SaveAsync(params object[] rows)
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    private async Task<string> OfferingAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await db.VmOfferings.FirstAsync()).Id;
    }

    private Vm NewVm(string offering, string name, Guid? networkId = null) =>
        Vm.Create(_tenant.Id, networkId ?? Guid.NewGuid(), $"{name}-{Guid.NewGuid():N}"[..20], offering, "tpl-1", "cluster-a", 20);

    private Vm SeenVm(string offering, string name, VmStatus state, Guid? networkId = null)
    {
        Vm vm = NewVm(offering, name, networkId);
        vm.MarkRunning($"vm{Guid.NewGuid():N}"[..20], "10.0.0.5");
        vm.MarkObserved(state, null);
        return vm;
    }

    // A VM Snapshotting is on Incus with a snapshot create or revert in flight.
    private Vm SnapshottingVm(string offering, string name, Guid? networkId = null)
    {
        Vm vm = SeenVm(offering, name, VmStatus.Running, networkId);
        vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "s");
        Assert.Equal(VmStatus.Snapshotting, vm.Status);
        return vm;
    }

    [Fact]
    public async Task VmQuota_CountsWhatExistsOrIsBeingCreated_NotAFailedCreate_NotFound_OrADeleteAsked()
    {
        string offering = await OfferingAsync();
        Vm failed = NewVm(offering, "failed");
        failed.MarkRequestFailed("refused", null);
        Vm lost = SeenVm(offering, "lost", VmStatus.Running);
        lost.MarkNotFound();
        Vm deleting = SeenVm(offering, "deleting", VmStatus.Running);
        deleting.Delete(Guid.NewGuid());
        Vm snapshotting = SnapshottingVm(offering, "snapshotting");
        await SaveAsync(NewVm(offering, "creating"), SeenVm(offering, "stopped", VmStatus.Stopped), snapshotting, failed, lost,
            deleting);

        await using TitanDbContext db = pg.NewDbContext();
        Assert.Equal(3, await new VmRepository(db).CountActiveByTenantAsync(_tenant.Id)); // creating, stopped, snapshotting
        Assert.True(snapshotting.OccupiesQuota);
    }

    [Fact]
    public async Task VmBilling_IsEveryStatusWithItsQemuAlive_NeverStoppedNorAbsent()
    {
        string offering = await OfferingAsync();
        var vms = new[]
        {
            SeenVm(offering, "running", VmStatus.Running), SeenVm(offering, "ready", VmStatus.Ready),
            SeenVm(offering, "frozen", VmStatus.Frozen), SeenVm(offering, "error", VmStatus.Error),
            SeenVm(offering, "stopped", VmStatus.Stopped), NewVm(offering, "creating"),
        };
        await SaveAsync(vms.Cast<object>().ToArray());

        await using TitanDbContext db = pg.NewDbContext();
        var billed = (await new VmRepository(db).ListBillableAsync()).Where(v => v.TenantId == _tenant.Id).Select(v => v.Id);

        Assert.Equal(vms.Take(4).Select(v => v.Id).OrderBy(i => i), billed.OrderBy(i => i));
    }

    // A VM holds its network while Incus has the instance (a delete in flight included) or its create waits
    // (titan-ops features/vm/design.md#counting-and-billing); one network per shape, so one holder cannot hide another.
    [Fact]
    public async Task NetworkIsHeld_ByAVmIncusHoldsOrIsAboutTo_NotByAFailedCreateNotFoundOrADeletedOne()
    {
        string offering = await OfferingAsync();
        Guid deletingNetwork = Guid.NewGuid(), creatingNetwork = Guid.NewGuid(), failedNetwork = Guid.NewGuid(),
            lostNetwork = Guid.NewGuid(), deletedNetwork = Guid.NewGuid(), snapshottingNetwork = Guid.NewGuid();
        Vm deleting = SeenVm(offering, "deleting", VmStatus.Running, deletingNetwork);
        deleting.Delete(Guid.NewGuid());
        Vm failed = NewVm(offering, "failed", failedNetwork);
        failed.MarkRequestFailed("refused", null);
        Vm lost = SeenVm(offering, "lost", VmStatus.Running, lostNetwork);
        lost.MarkNotFound();
        Vm deleted = SeenVm(offering, "deleted", VmStatus.Running, deletedNetwork);
        deleted.Delete(Guid.NewGuid());
        deleted.MarkDeleted();
        Vm snapshotting = SnapshottingVm(offering, "snapshotting", snapshottingNetwork);
        await SaveAsync(deleting, NewVm(offering, "creating", creatingNetwork), failed, lost, deleted, snapshotting);

        await using TitanDbContext db = pg.NewDbContext();
        var repo = new NetworkRepository(db);
        Assert.True(await repo.HasActiveVmsAsync(deletingNetwork));
        Assert.True(await repo.HasActiveVmsAsync(creatingNetwork));
        Assert.True(await repo.HasActiveVmsAsync(snapshottingNetwork));
        Assert.True(snapshotting.HasNetwork);
        Assert.False(await repo.HasActiveVmsAsync(failedNetwork));
        Assert.False(await repo.HasActiveVmsAsync(lostNetwork));
        Assert.False(await repo.HasActiveVmsAsync(deletedNetwork));
    }

    private Volume NewVolume(string name) => Volume.Create(_tenant.Id, $"{name}-{Guid.NewGuid():N}"[..20], "cluster-a", "standard", 10);

    private Volume OnIncus(string name)
    {
        Volume volume = NewVolume(name);
        volume.MarkCreated($"vol{Guid.NewGuid():N}"[..20]);
        return volume;
    }

    // Quota counts a volume Incus holds or is about to hold; billing, a volume Incus holds, an attach, a detach or a delete
    // in flight included while it was last seen holding it (titan-ops features/volume/design.md#counting-and-billing).
    [Fact]
    public async Task VolumeQuotaAndBilling_FollowWhatIncusHolds()
    {
        string offering = await OfferingAsync();
        Vm vm = SeenVm(offering, "vm-for-volumes", VmStatus.Running);
        Volume creating = NewVolume("creating");
        // Seen on Incus while its create still waits: only the Domain reaches it, the reconciler lists only volumes with a
        // provider id; the SQL counts and bills it as the bools do.
        Volume creatingSeen = NewVolume("creating-seen");
        creatingSeen.MarkObserved(null, false);
        Volume available = OnIncus("available");
        Volume attachingHeld = OnIncus("attaching-held");
        attachingHeld.Attach(vm);
        Volume attachingLost = OnIncus("attaching-lost");
        attachingLost.Attach(vm);
        attachingLost.MarkNotFound();
        Volume detachingHeld = OnIncus("detaching-held");
        detachingHeld.Attach(vm);
        detachingHeld.MarkAttached(vm.Id);
        detachingHeld.Detach();
        Volume detachingLost = OnIncus("detaching-lost");
        detachingLost.Attach(vm);
        detachingLost.MarkAttached(vm.Id);
        detachingLost.Detach();
        detachingLost.MarkNotFound();
        Volume deleting = OnIncus("deleting");
        deleting.Delete();
        Volume deletingLost = OnIncus("deleting-lost");
        deletingLost.Delete();
        deletingLost.MarkNotFound();
        Volume failed = NewVolume("failed");
        failed.MarkRequestFailed("could not create", "raw");
        Volume lost = OnIncus("lost");
        lost.MarkNotFound();
        Volume deleted = OnIncus("deleted");
        deleted.Delete();
        deleted.MarkDeleted();
        Volume[] all =
        [
            creating, creatingSeen, available, attachingHeld, attachingLost, detachingHeld, detachingLost, deleting,
            deletingLost, failed, lost, deleted,
        ];
        await SaveAsync(all);

        Guid[] counted = [creating.Id, creatingSeen.Id, available.Id, attachingHeld.Id, detachingHeld.Id];
        Guid[] billedIds = [creatingSeen.Id, available.Id, attachingHeld.Id, detachingHeld.Id, deleting.Id];
        Assert.Equal(counted.Order(), all.Where(v => v.OccupiesQuota).Select(v => v.Id).Order());
        Assert.Equal(billedIds.Order(), all.Where(v => v.IsBilled).Select(v => v.Id).Order());
        await using TitanDbContext db = pg.NewDbContext();
        var repo = new VolumeRepository(db);
        Assert.Equal(counted.Length, await repo.CountActiveByTenantAsync(_tenant.Id));
        var billed = (await repo.ListBillableAsync()).Where(v => v.TenantId == _tenant.Id).Select(v => v.Id);
        Assert.Equal(billedIds.Order(), billed.Order()); // a delete in flight still bills
    }

    // A volume snapshot on Incus, its create ended Done.
    private static VolumeSnapshot CreatedVolumeSnapshot(Volume volume, string name)
    {
        VolumeSnapshot snapshot = volume.WithSnapshot(name);
        snapshot.MarkCreated($"{volume.ExternalId}/{name}");
        return snapshot;
    }

    // A snapshot on Incus, its create ended Done.
    private static VmSnapshot CreatedSnapshot(Vm vm, string name)
    {
        VmSnapshot snapshot = vm.WithSnapshot(name);
        snapshot.MarkCreated($"{vm.ExternalId}/{name}");
        return snapshot;
    }

    // Billed while Incus holds the snapshot: Available at rest, or an operation waiting on one Incus was last seen holding
    // (titan-ops features/vm/design.md#vm-snapshot-counting). A VM takes one create or revert at a time, so each waiting
    // create or revert is on a VM of its own; deletes wait side by side on one VM.
    [Fact]
    public async Task SnapshotBilling_IsWhatIncusHolds()
    {
        string offering = await OfferingAsync();
        Vm atRest = SeenVm(offering, "vm-rest", VmStatus.Running);
        VmSnapshot available = CreatedSnapshot(atRest, "available");
        VmSnapshot notFound = CreatedSnapshot(atRest, "not-found");
        atRest.MarkSnapshotNotFound(notFound.Id);
        VmSnapshot failed = atRest.WithSnapshot("failed");
        failed.MarkRequestFailed("refused", null);
        VmSnapshot deletingHeld = CreatedSnapshot(atRest, "deleting-held");
        atRest.DeleteSnapshot(deletingHeld.Id, Guid.NewGuid());
        VmSnapshot deletingLost = CreatedSnapshot(atRest, "deleting-lost");
        atRest.DeleteSnapshot(deletingLost.Id, Guid.NewGuid());
        atRest.MarkSnapshotNotFound(deletingLost.Id);
        VmSnapshot deleted = CreatedSnapshot(atRest, "deleted");
        atRest.DeleteSnapshot(deleted.Id, Guid.NewGuid());
        deleted.MarkDeleted();

        Vm creatingVm = SeenVm(offering, "vm-creating", VmStatus.Running);
        VmSnapshot creating = creatingVm.WithSnapshot("creating");
        // Seen on Incus while its create still waits: only the Domain reaches it, the reconciler lists only snapshots
        // with a provider id; the SQL bills it as IsBilled does.
        Vm creatingSeenVm = SeenVm(offering, "vm-creating-seen", VmStatus.Running);
        VmSnapshot creatingSeen = creatingSeenVm.WithSnapshot("creating-seen");
        creatingSeenVm.MarkSnapshotObserved(creatingSeen.Id);
        Vm revertingHeldVm = SeenVm(offering, "vm-reverting-held", VmStatus.Running);
        VmSnapshot revertingHeld = CreatedSnapshot(revertingHeldVm, "reverting-held");
        revertingHeldVm.RevertSnapshot(revertingHeld.Id, Guid.NewGuid());
        Vm revertingLostVm = SeenVm(offering, "vm-reverting-lost", VmStatus.Running);
        VmSnapshot revertingLost = CreatedSnapshot(revertingLostVm, "reverting-lost");
        revertingLostVm.RevertSnapshot(revertingLost.Id, Guid.NewGuid());
        revertingLostVm.MarkSnapshotNotFound(revertingLost.Id);

        Volume volume = OnIncus("vol");
        volume.WithSnapshot("vol-creating");
        VolumeSnapshot volumeReady = CreatedVolumeSnapshot(volume, "vol-ready");
        VolumeSnapshot volumeNotFound = CreatedVolumeSnapshot(volume, "vol-not-found");
        volume.MarkSnapshotNotFound(volumeNotFound.Id);
        VolumeSnapshot volumeFailed = volume.WithSnapshot("vol-failed");
        volumeFailed.MarkRequestFailed("refused", null);
        VolumeSnapshot volumeDeletingHeld = CreatedVolumeSnapshot(volume, "vol-deleting-held");
        volume.DeleteSnapshot(volumeDeletingHeld.Id);
        VolumeSnapshot volumeDeletingLost = CreatedVolumeSnapshot(volume, "vol-deleting-lost");
        volume.DeleteSnapshot(volumeDeletingLost.Id);
        volume.MarkSnapshotNotFound(volumeDeletingLost.Id);
        // Seen while its create still waits: Domain only, as for a VM snapshot.
        VolumeSnapshot volumeCreatingSeen = volume.WithSnapshot("vol-creating-seen");
        volume.MarkSnapshotObserved(volumeCreatingSeen.Id);
        Vm[] vms = [atRest, creatingVm, creatingSeenVm, revertingHeldVm, revertingLostVm];
        await SaveAsync([.. vms, volume]);

        VmSnapshot[] all =
        [
            available, notFound, failed, deletingHeld, deletingLost, deleted, creating, creatingSeen, revertingHeld,
            revertingLost,
        ];
        Guid[] expected = [available.Id, deletingHeld.Id, creatingSeen.Id, revertingHeld.Id];
        Assert.Equal(expected.Order(), all.Where(s => s.IsBilled).Select(s => s.Id).Order());
        HashSet<Guid> vmIds = vms.Select(v => v.Id).ToHashSet();
        await using TitanDbContext db = pg.NewDbContext();
        var vmBilled = (await new VmSnapshotRepository(db).ListBillableAsync()).Where(s => vmIds.Contains(s.VmId)).Select(s => s.Id);
        var volumeBilled = (await new VolumeSnapshotRepository(db).ListBillableAsync()).Where(s => s.VolumeId == volume.Id).Select(s => s.Id);
        Assert.Equal(expected.Order(), vmBilled.Order());
        Assert.Equal(new[] { volumeReady.Id, volumeDeletingHeld.Id, volumeCreatingSeen.Id }.Order(), volumeBilled.Order());
    }

    // What customers in a cluster may pick: the cluster holds the alias, or a re-sync waits and saw it there
    // (titan-ops features/catalog/design.md#template-customer-read). The LINQ filter reads the owned operation's
    // last-seen column; InMemory runs it as C#, so only Postgres shows it translates.
    [Fact]
    public async Task TemplateSyncedList_IsAnAvailableCluster_OrASyncThatSawTheAliasThere()
    {
        string offering = await OfferingAsync();
        string cluster = $"qr-{Guid.NewGuid():N}"[..16];

        VmTemplate Template(string name) =>
            VmTemplate.Create($"{name}-{Guid.NewGuid():N}"[..20], null, "Ubuntu-22.04", null, offering);
        VmTemplate Available(string name)
        {
            VmTemplate template = Template(name);
            Guid syncId = template.SyncIn(cluster);
            template.SyncSucceededIn(cluster, syncId, $"tpl-{template.Id:N}");
            return template;
        }
        VmTemplate Lost(string name)
        {
            VmTemplate template = Available(name);
            template.MarkNotFoundIn(cluster);
            return template;
        }

        VmTemplate available = Available("available");
        VmTemplate notFound = Lost("not-found");
        VmTemplate resyncSeen = Lost("resync-seen");
        resyncSeen.SyncIn(cluster);
        resyncSeen.MarkObservedIn(cluster);
        VmTemplate resyncLost = Lost("resync-lost");
        resyncLost.SyncIn(cluster);
        VmTemplate failed = Template("failed");
        Guid failedSyncId = failed.SyncIn(cluster);
        failed.SyncFailedIn(cluster, failedSyncId, null, "no image for this architecture", null);
        VmTemplate firstSync = Template("first-sync");
        firstSync.SyncIn(cluster);
        VmTemplate[] all = [available, notFound, resyncSeen, resyncLost, failed, firstSync];
        await SaveAsync([.. all]);

        Guid[] expected = [available.Id, resyncSeen.Id];
        Assert.Equal(expected.Order(), all.Where(t => t.IsSyncedIn(cluster)).Select(t => t.Id).Order());
        HashSet<Guid> ids = all.Select(t => t.Id).ToHashSet();
        await using TitanDbContext db = pg.NewDbContext();
        var repository = new VmTemplateRepository(db);
        var inCluster = (await repository.ListSyncedInClusterAsync(cluster)).Where(t => ids.Contains(t.Id)).Select(t => t.Id);
        var inAnyCluster = (await repository.ListSyncedInAnyClusterAsync()).Where(t => ids.Contains(t.Id)).Select(t => t.Id);
        Assert.Equal(expected.Order(), inCluster.Order());
        Assert.Equal(expected.Order(), inAnyCluster.Order());
    }

    private Network NewNetwork(string name, bool publicIpv6)
    {
        Network network = Network.Create(_tenant.Id, NetworkName.Parse($"{name}-{Guid.NewGuid():N}"[..20]), "cluster-a", null, publicIpv6);
        if (publicIpv6)
        {
            network.ReceivePublicIpv6($"2001:db8:{Random.Shared.Next(0x1000, 0xffff):x}:{Random.Shared.Next(0x1000, 0xffff):x}::/64");
        }
        return network;
    }

    private Network ActiveNetwork(string name, bool publicIpv6)
    {
        Network network = NewNetwork(name, publicIpv6);
        network.MarkActive($"net{Guid.NewGuid():N}"[..20], null, null);
        return network;
    }

    [Fact]
    public async Task NetworkAndPublicIpv6Quota_CountWhatIncusHoldsOrIsAboutTo_TheIpv6OneOnlyWithASubnet()
    {
        Network leaving = ActiveNetwork("leaving", publicIpv6: true);
        leaving.Delete();
        Network failed = NewNetwork("failed", publicIpv6: false);
        failed.MarkRequestFailed("reason", "detail");
        Network lost = ActiveNetwork("lost", publicIpv6: true);
        lost.MarkNotFound();
        await SaveAsync(
            NewNetwork("creating-v6", publicIpv6: true), ActiveNetwork("active-v6", publicIpv6: true),
            ActiveNetwork("active", publicIpv6: false), leaving, failed, lost);

        await using TitanDbContext db = pg.NewDbContext();
        var repo = new NetworkRepository(db);
        Assert.Equal(3, await repo.CountActiveByTenantAsync(_tenant.Id)); // creating-v6, active-v6, active
        Assert.Equal(2, await repo.CountPublicIpv6ActiveByTenantAsync(_tenant.Id)); // creating-v6, active-v6
    }

    private const string Subnet = "2001:41d0:d00:d405::/64";

    private LoadBalancer NewLoadBalancer(Guid networkId, int vipIndex, Vm backend) =>
        LoadBalancer.Create(_tenant.Id, $"lb-{Guid.NewGuid():N}"[..12], networkId, "net-ext", Subnet, "cluster-a",
            LoadBalancerVip.For(Subnet, vipIndex), [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [backend]);

    // One row per shape (titan-ops features/load-balancer/design.md#lb-quota-and-billing): the SQL reads status and
    // observed_state, so an update counts only while Incus was seen holding the load balancer.
    [Fact]
    public async Task LoadBalancerQuota_CountsWhatExistsOrIsBeingCreated_NotAFailedCreate_OrADeleteAsked()
    {
        string offering = await OfferingAsync();
        Guid networkId = Guid.NewGuid();
        Vm backend = SeenVm(offering, "backend", VmStatus.Running, networkId);
        LoadBalancerListenerSpec[] other = [new(LoadBalancerProtocol.UDP, 53, 53)];
        LoadBalancer created = NewLoadBalancer(networkId, 1, backend);
        created.MarkCreated(created.Vip);
        LoadBalancer leaving = NewLoadBalancer(networkId, 2, backend);
        leaving.MarkCreated(leaving.Vip);
        leaving.Delete();
        LoadBalancer failed = NewLoadBalancer(networkId, 3, backend);
        failed.MarkRequestFailed("refused", null);
        LoadBalancer updatingSeen = NewLoadBalancer(networkId, 5, backend);
        updatingSeen.MarkCreated(updatingSeen.Vip);
        updatingSeen.Update(other, [backend]);
        LoadBalancer updatingLost = NewLoadBalancer(networkId, 6, backend);
        updatingLost.MarkCreated(updatingLost.Vip);
        updatingLost.Update(other, [backend]);
        updatingLost.MarkNotFound();
        LoadBalancer notFound = NewLoadBalancer(networkId, 7, backend);
        notFound.MarkCreated(notFound.Vip);
        notFound.MarkNotFound();
        LoadBalancer leavingLost = NewLoadBalancer(networkId, 8, backend);
        leavingLost.MarkCreated(leavingLost.Vip);
        leavingLost.MarkNotFound();
        leavingLost.Delete();
        LoadBalancer deleted = NewLoadBalancer(networkId, 9, backend);
        deleted.MarkCreated(deleted.Vip);
        deleted.Delete();
        deleted.MarkDeleted();
        LoadBalancer creating = NewLoadBalancer(networkId, 4, backend);
        LoadBalancer[] all = [creating, created, leaving, failed, updatingSeen, updatingLost, notFound, leavingLost, deleted];
        await SaveAsync(all);

        await using TitanDbContext db = pg.NewDbContext();
        // creating, created, updating (seen)
        Assert.Equal(3, await new LoadBalancerRepository(db).CountActiveByTenantAsync(_tenant.Id));
        Assert.Equal(3, all.Count(lb => lb.OccupiesQuota));
    }
}
