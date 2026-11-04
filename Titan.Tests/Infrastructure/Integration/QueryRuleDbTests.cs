using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The quota and billing queries on the SQL EF generates from the Domain rules (titan-ops
/// features/vm/design.md#counting-and-billing, features/volume/design.md#counting-and-billing,
/// features/network/design.md#ipv6-quota, features/load-balancer/design.md#lb-quota-and-billing), one row per side of
/// each condition. The repository state tests run the same rules on InMemory, which runs the expression, not the SQL.
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

    private Vm SeenVm(string offering, string name, VmState state, Guid? networkId = null)
    {
        Vm vm = NewVm(offering, name, networkId);
        vm.MarkRunning($"vm{Guid.NewGuid():N}"[..20], "10.0.0.5");
        vm.MarkObserved(state, null);
        return vm;
    }

    [Fact]
    public async Task VmQuota_CountsWhatExistsOrIsBeingCreated_NotAFailedCreate_NotFound_OrADeleteAsked()
    {
        string offering = await OfferingAsync();
        Vm failed = NewVm(offering, "failed");
        failed.MarkRequestFailed("refused", null);
        Vm lost = SeenVm(offering, "lost", VmState.Running);
        lost.MarkNotFound();
        Vm deleting = SeenVm(offering, "deleting", VmState.Running);
        deleting.RequestDelete();
        await SaveAsync(NewVm(offering, "creating"), SeenVm(offering, "stopped", VmState.Stopped), failed, lost, deleting);

        await using TitanDbContext db = pg.NewDbContext();
        Assert.Equal(2, await new VmRepository(db).CountActiveByTenantAsync(_tenant.Id));
    }

    [Fact]
    public async Task VmBilling_IsEveryStatusWithItsQemuAlive_NeverStoppedNorAbsent()
    {
        string offering = await OfferingAsync();
        var vms = new[]
        {
            SeenVm(offering, "running", VmState.Running), SeenVm(offering, "ready", VmState.Ready),
            SeenVm(offering, "frozen", VmState.Frozen), SeenVm(offering, "error", VmState.Error),
            SeenVm(offering, "stopped", VmState.Stopped), NewVm(offering, "creating"),
        };
        await SaveAsync(vms.Cast<object>().ToArray());

        await using TitanDbContext db = pg.NewDbContext();
        var billed = (await new VmRepository(db).ListBillableAsync()).Where(v => v.TenantId == _tenant.Id).Select(v => v.Id);

        Assert.Equal(vms.Take(4).Select(v => v.Id).OrderBy(i => i), billed.OrderBy(i => i));
    }

    private Volume NewVolume(string name) => Volume.Create(_tenant.Id, $"{name}-{Guid.NewGuid():N}"[..20], "cluster-a", "standard", 10);

    private Volume OnIncus(string name)
    {
        Volume volume = NewVolume(name);
        volume.MarkCreated($"vol{Guid.NewGuid():N}"[..20]);
        return volume;
    }

    [Fact]
    public async Task VolumeQuotaAndBilling_FollowWhatIncusHolds()
    {
        Volume creating = NewVolume("creating");
        Volume available = OnIncus("available");
        Volume deleting = OnIncus("deleting");
        deleting.RequestDelete();
        Volume failed = NewVolume("failed");
        failed.MarkRequestFailed("could not create", "raw");
        Volume lost = OnIncus("lost");
        lost.MarkNotFound();
        await SaveAsync(creating, available, deleting, failed, lost);

        await using TitanDbContext db = pg.NewDbContext();
        var repo = new VolumeRepository(db);
        Assert.Equal(2, await repo.CountActiveByTenantAsync(_tenant.Id)); // creating, available
        var billed = (await repo.ListBillableAsync()).Where(v => v.TenantId == _tenant.Id).Select(v => v.Id).OrderBy(i => i);
        Assert.Equal(new[] { available.Id, deleting.Id }.OrderBy(i => i), billed); // a delete in flight still bills
    }

    [Fact]
    public async Task SnapshotBilling_IsWhatIncusHolds()
    {
        string offering = await OfferingAsync();
        Vm vm = SeenVm(offering, "vm", VmState.Running);
        vm.WithSnapshot("vm-creating");
        VmSnapshot vmReady = vm.WithSnapshot("vm-ready");
        vmReady.MarkCreated($"{vm.ExternalId}/s1");
        Volume volume = OnIncus("vol");
        volume.WithSnapshot("vol-creating");
        VolumeSnapshot volumeReady = volume.WithSnapshot("vol-ready");
        volumeReady.MarkCreated("vol/s1");
        await SaveAsync(vm, volume);

        await using TitanDbContext db = pg.NewDbContext();
        var vmBilled = (await new VmSnapshotRepository(db).ListBillableAsync()).Where(s => s.VmId == vm.Id).Select(s => s.Id);
        var volumeBilled = (await new VolumeSnapshotRepository(db).ListBillableAsync()).Where(s => s.VolumeId == volume.Id).Select(s => s.Id);
        Assert.Equal(new[] { vmReady.Id }, vmBilled);
        Assert.Equal(new[] { volumeReady.Id }, volumeBilled);
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
        leaving.RequestDelete();
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

    [Fact]
    public async Task LoadBalancerQuota_CountsWhatExistsOrIsBeingCreated_NotAFailedCreate_OrADeleteAsked()
    {
        string offering = await OfferingAsync();
        Guid networkId = Guid.NewGuid();
        Vm backend = SeenVm(offering, "backend", VmState.Running, networkId);
        LoadBalancer created = NewLoadBalancer(networkId, 1, backend);
        created.MarkCreated(created.Vip);
        LoadBalancer leaving = NewLoadBalancer(networkId, 2, backend);
        leaving.MarkCreated(leaving.Vip);
        leaving.RequestDelete();
        LoadBalancer failed = NewLoadBalancer(networkId, 3, backend);
        failed.MarkRequestFailed("refused", null);
        await SaveAsync(NewLoadBalancer(networkId, 4, backend), created, leaving, failed);

        await using TitanDbContext db = pg.NewDbContext();
        Assert.Equal(2, await new LoadBalancerRepository(db).CountActiveByTenantAsync(_tenant.Id)); // creating, created
    }
}
