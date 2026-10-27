using Titan.Domain.Values;
using Microsoft.Extensions.Options;
using Moq;
using Titan.Application.Options;
using Titan.Application.Services.Billing;
using Titan.Application.Services.Billing.Metering;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Aggregates.Catalog.VolumeOffering;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Domain.Repositories.Compute;

namespace Titan.Tests.Services;

// Occupying storage is billed regardless of the attached VM's state: a volume Attached to a Stopped VM
// still bills, and a detached Allocated volume still bills.
public class DiskMeterServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 9, 10, 0, TimeSpan.Zero);
    private const string OfferingId = "vo-std";
    // The tier's own price column (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#price-belongs-to-offering).
    private const decimal Rate = 150m;   // VND/GB/h — whole-đồng so the rounded Cost is exact (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#money-is-long-vnd)

    private readonly Mock<IVolumeRepository> _volumeRepo = new();
    private readonly FakeVolumeOfferingRepository _offerings = new();
    private readonly FakeUsageRecordRepository _usageRepo = new();

    public DiskMeterServiceTests() =>
        _offerings.Seed(VolumeOffering.Create(OfferingId, "Standard", 0, true, Rate));

    private DiskMeterService CreateService(int intervalMinutes = 10) => new(
        _volumeRepo.Object, _usageRepo, _offerings,
        Options.Create(new BillingMeterOptions { IntervalMinutes = intervalMinutes }));

    private static Volume MakeVolume(int sizeGb = 40, string offeringId = OfferingId) =>
        Volume.CreateUnchecked(TenantId, "data-vol", "cluster-1", offeringId, sizeGb);

    private void SeedVolumes(params Volume[] volumes) =>
        _volumeRepo.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(volumes.ToList());

    // Quantity = SizeGb.
    [Fact]
    public async Task RunTick_AllocatedVolume_InsertsOneDiskRecord()
    {
        var volume = MakeVolume(40);
        volume.MarkCreated("ext-vol-1");
        SeedVolumes(volume);

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(1, inserted);
        var record = Assert.Single(_usageRepo.All);
        Assert.Equal(UsageResourceType.Disk, record.ResourceType);
        Assert.Equal(volume.Id, record.ResourceId);
        Assert.Equal(40, record.Quantity);
        Assert.Equal(1000m, record.Cost);   // 150 × 10/60 × 40 = 1000 VND
        Assert.Equal(Rate, record.UnitPricePerHour.PerUnit);
        Assert.Equal(BillingConstants.DefaultCurrency, record.Currency);
    }

    // Independent of VM state: an Attached volume still bills regardless of the VM it is
    // attached to (the meter never looks at the VM at all).
    [Fact]
    public async Task RunTick_AttachedVolume_StillBillsRegardlessOfVmState()
    {
        var volume = MakeVolume(80);
        volume.MarkCreated("ext-vol-2");
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-1", 20);
        vm.MarkRunning("ext-vm-1", "10.0.0.5");
        volume.Attach(vm);
        volume.MarkObserved(vm.Id, false);
        SeedVolumes(volume);

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(1, inserted);
        Assert.Single(_usageRepo.All);
    }

    // Which volumes occupy billable storage is the repository's question
    // (IVolumeRepository.ListBillableAsync), pinned per situation by
    // VolumeRepositoryStateTests.Billing_IsWhatIncusHolds_ADeleteInFlightIncluded.

    // Idempotent on (volumeId, bucket), same mechanism as the compute meter.
    [Fact]
    public async Task RunTick_CalledTwiceInSameBucket_InsertsOnlyOneRecord()
    {
        var volume = MakeVolume();
        volume.MarkCreated("ext-vol-3");
        SeedVolumes(volume);

        var first = await CreateService().RunTickAsync(Now, default);
        var second = await CreateService().RunTickAsync(Now.AddMinutes(2), default); // same 10-min bucket

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(_usageRepo.All);
    }

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#price-belongs-to-offering — same rule as the compute meter: a volume pointing at a tier that is not in the
    // catalog is damaged data, not a zero price. Refuse to bill rather than write a silent 0.
    [Fact]
    public async Task RunTick_OfferingMissing_ThrowsAndWritesNothing()
    {
        var volume = MakeVolume(40, "tier-that-was-deleted");
        volume.MarkCreated("ext-vol-x");
        SeedVolumes(volume);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().RunTickAsync(Now, default));

        Assert.Contains("tier-that-was-deleted", ex.Message);
        Assert.Empty(_usageRepo.All);
    }

    // One volume without a price refuses the whole tick, wherever the repository happens to return it
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/billing/metering/design.md#missing-offering): a half-billed tick bills some tenants and not others.
    [Fact]
    public async Task RunTick_MissingOfferingBeforeAGoodVolume_WritesNothing()
    {
        var broken = MakeVolume(40, "tier-that-was-deleted");
        broken.MarkCreated("ext-vol-x");
        var good = MakeVolume(40);
        good.MarkCreated("ext-vol-1");
        SeedVolumes(broken, good);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().RunTickAsync(Now, default));

        Assert.Empty(_usageRepo.All);
    }

    [Fact]
    public async Task RunTick_MissingOfferingAfterAGoodVolume_WritesNothing()
    {
        var good = MakeVolume(40);
        good.MarkCreated("ext-vol-1");
        var broken = MakeVolume(40, "tier-that-was-deleted");
        broken.MarkCreated("ext-vol-x");
        SeedVolumes(good, broken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateService().RunTickAsync(Now, default));

        Assert.Empty(_usageRepo.All);
    }
}
