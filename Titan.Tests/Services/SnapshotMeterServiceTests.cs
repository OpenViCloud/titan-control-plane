using Titan.Domain.Values;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Aggregates.Compute.Vm;
using Microsoft.Extensions.Options;
using Moq;
using Titan.Application.Options;
using Titan.Application.Services.Billing;
using Titan.Application.Services.Billing.Metering;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Aggregates.Catalog.SnapshotOffering;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using Titan.Domain.Aggregates.Compute.VolumeSnapshot;
using Titan.Domain.Repositories.Compute;

namespace Titan.Tests.Services;

// Snapshots (VM + volume) occupy datastore capacity independently of any live VM/volume. Flat
// per-snapshot rate: Titan tracks no snapshot size, so there is nothing to bill per-GB.
public class SnapshotMeterServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 9, 10, 0, TimeSpan.Zero);
    private const decimal Rate = 6000m;   // VND/h — whole-đồng so rounded Cost is exact (titan-ops architecture/money-and-wallet.md#money-is-long-vnd)

    private readonly Mock<IVmSnapshotRepository> _vmSnaps = new();
    private readonly Mock<IVolumeSnapshotRepository> _volSnaps = new();
    private readonly FakeSnapshotOfferingRepository _offerings = new();
    private readonly FakeUsageRecordRepository _usageRepo = new();

    public SnapshotMeterServiceTests() =>
        _offerings.Seed(SnapshotOffering.Create(SnapshotOffering.DefaultId, "Standard", Rate));

    private SnapshotMeterService CreateService(int intervalMinutes = 10) => new(
        _vmSnaps.Object, _volSnaps.Object, _usageRepo, _offerings,
        Options.Create(new BillingMeterOptions { IntervalMinutes = intervalMinutes }));

    private void SeedVmSnaps(params VmSnapshot[] snaps) =>
        _vmSnaps.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snaps.ToList());

    private void SeedVolSnaps(params VolumeSnapshot[] snaps) =>
        _volSnaps.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(snaps.ToList());

    // The factories require the parent in a state that permits a snapshot (titan-ops architecture/intent-and-observation.md#validate-inside-the-mutation), so the
    // seed builds one. That is the invariant doing its job: there is no way to make a snapshot of a
    // VM that was never running, in production or in a test.
    private static VmSnapshot ReadyVmSnapshot(string name = "vm-snap")
    {
        var vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-1", 20);
        vm.MarkRunning("ext-vm", "10.0.0.5");
        var snap = VmSnapshot.CreateUnchecked(vm.Id, TenantId, name);
        snap.MarkCreated("ext-vm-snap");
        return snap;
    }

    private static VolumeSnapshot BackedUpVolumeSnapshot(string name = "vol-snap")
    {
        var vol = Volume.CreateUnchecked(TenantId, "vol-1", "cluster-1", "offering-1", 50);
        vol.MarkCreated("ext-vol");
        var snap = VolumeSnapshot.CreateUnchecked(vol.Id, TenantId, name);
        snap.MarkCreated("ext-vol-snap");
        return snap;
    }

    // A Ready VM snapshot bills: one SNAPSHOT record, flat rate, Quantity = 1.
    [Fact]
    public async Task RunTick_ReadyVmSnapshot_InsertsOneRateRecord()
    {
        var snap = ReadyVmSnapshot();
        SeedVmSnaps(snap);
        SeedVolSnaps();

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(1, inserted);
        var record = Assert.Single(_usageRepo.All);
        Assert.Equal(UsageResourceType.Snapshot, record.ResourceType);
        Assert.Equal(snap.Id, record.ResourceId);
        Assert.Equal(1, record.Quantity);                       // flat per-snapshot, not per-GB
        Assert.Equal(1000m, record.Cost);                       // 6000 VND/h × 10min = 1000 VND
        Assert.Equal(Rate, record.UnitPricePerHour.PerUnit);            // from the snapshot offering (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering)
        Assert.Equal(BillingConstants.DefaultCurrency, record.Currency);
    }

    // A volume snapshot Incus holds bills too — the meter covers both aggregates.
    [Fact]
    public async Task RunTick_BackedUpVolumeSnapshot_InsertsOneRecord()
    {
        SeedVmSnaps();
        SeedVolSnaps(BackedUpVolumeSnapshot());

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(1, inserted);
        Assert.Equal(UsageResourceType.Snapshot, Assert.Single(_usageRepo.All).ResourceType);
    }

    [Fact]
    public async Task RunTick_BothTypes_InsertsOnePerSnapshot()
    {
        SeedVmSnaps(ReadyVmSnapshot("a"), ReadyVmSnapshot("b"));
        SeedVolSnaps(BackedUpVolumeSnapshot("c"));

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(3, inserted);
        Assert.Equal(3, _usageRepo.All.Count);
    }

    // Which snapshots occupy the datastore is the repositories' question (ListBillableAsync), pinned per
    // situation in VolumeRepositoryStateTests and VmSnapshotFirewallRepositoryStateTests (a revert in
    // flight still bills, a delete in flight does not).
    [Fact]
    public async Task RunTick_AsksBothRepositoriesForTheirBillableSnapshots()
    {
        SeedVmSnaps();
        SeedVolSnaps();

        await CreateService().RunTickAsync(Now, default);

        _vmSnaps.Verify(r => r.ListBillableAsync(It.IsAny<CancellationToken>()), Times.Once);
        _volSnaps.Verify(r => r.ListBillableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // Idempotent on (snapshotId, bucket): two ticks in the same bucket insert once.
    [Fact]
    public async Task RunTick_CalledTwiceInSameBucket_InsertsOnlyOneRecord()
    {
        SeedVmSnaps(ReadyVmSnapshot());
        SeedVolSnaps();

        var first = await CreateService().RunTickAsync(Now, default);
        var second = await CreateService().RunTickAsync(Now.AddMinutes(2), default); // same 10-min bucket

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(_usageRepo.All);
    }

    // Rate 0 disables snapshot billing but still inserts a 0-cost trail row: the row is
    // present, its cost is zero. This is a DECISION, and it is the reason the offering allows a zero
    // price where the VM and volume tiers do not.
    [Fact]
    public async Task RunTick_ZeroRate_InsertsRecordAtZeroCost()
    {
        SeedVmSnaps(ReadyVmSnapshot());
        SeedVolSnaps();
        var offerings = new FakeSnapshotOfferingRepository();
        offerings.Seed(SnapshotOffering.Create(SnapshotOffering.DefaultId, "Standard", 0m));

        var svc = new SnapshotMeterService(
            _vmSnaps.Object, _volSnaps.Object, _usageRepo, offerings,
            Options.Create(new BillingMeterOptions { IntervalMinutes = 10 }));
        await svc.RunTickAsync(Now, default);

        Assert.Equal(0m, Assert.Single(_usageRepo.All).Cost);
    }

    // Must NOT look like the zero-rate case above. A missing catalog row is a data-integrity fault:
    // the tick fails and writes nothing, so /healthz/meters reports it rather than snapshots
    // quietly billing zero. "Could not find the price" and "the price is zero" are different facts
    // and must not produce the same rows.
    [Fact]
    public async Task RunTick_OfferingMissing_ThrowsAndWritesNothing()
    {
        SeedVmSnaps(ReadyVmSnapshot());
        SeedVolSnaps();

        var svc = new SnapshotMeterService(
            _vmSnaps.Object, _volSnaps.Object, _usageRepo,
            new FakeSnapshotOfferingRepository(),   // empty catalog
            Options.Create(new BillingMeterOptions { IntervalMinutes = 10 }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RunTickAsync(Now, default));

        Assert.Contains(SnapshotOffering.DefaultId, ex.Message);
        Assert.Empty(_usageRepo.All);
    }
}
