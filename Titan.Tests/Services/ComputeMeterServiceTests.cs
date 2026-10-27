using Microsoft.Extensions.Options;
using Moq;
using Titan.Application.Options;
using Titan.Application.Services.Billing;
using Titan.Application.Services.Billing.Metering;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Aggregates.Catalog.VmOffering;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Repositories.Compute;

namespace Titan.Tests.Services;

// One INSERT per Running VM per tick, idempotent on (vm, bucket)
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#append-only-metering). A missed tick under-bills and never
// over-bills — the accepted trade-off. Price comes from the VM offering
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#price-belongs-to-offering).
public class ComputeMeterServiceTests
{
    private const string OfferingId = "m";
    private const decimal Price = 311.256m;

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 9, 10, 0, TimeSpan.Zero);

    private readonly Mock<IVmRepository> _vmRepo = new();
    private readonly FakeVmOfferingRepository _offerings = new();
    private readonly FakeUsageRecordRepository _usageRepo = new();

    private ComputeMeterService CreateService(int intervalMinutes = 10) => new(
        _vmRepo.Object, _usageRepo, _offerings,
        Options.Create(new BillingMeterOptions { IntervalMinutes = intervalMinutes }));

    private static VmOffering Offering(string id = OfferingId, decimal price = Price) =>
        VmOffering.Create(id, "Medium", 1.0m, 2, 4096, price);

    private static Vm RunningVm(string offeringId = OfferingId)
    {
        var vm = Vm.CreateUnchecked(TenantId, Guid.NewGuid(), "web-01", offeringId, "tmpl-1", "cluster-1", 40);
        vm.MarkRunning("ext-1", "10.0.0.1");
        return vm;
    }

    private void SeedRunningVms(params Vm[] vms) =>
        _vmRepo.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(vms.ToList());

    // One Running VM, one tick, exactly one COMPUTE record for that VM's bucket, priced from
    // that VM's tier.
    [Fact]
    public async Task RunTick_OneRunningVm_InsertsOneComputeRecord()
    {
        _offerings.Seed(Offering());
        var vm = RunningVm();
        SeedRunningVms(vm);

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(1, inserted);
        var record = Assert.Single(_usageRepo.All);
        Assert.Equal(UsageResourceType.Compute, record.ResourceType);
        Assert.Equal(vm.Id, record.ResourceId);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 9, 10, 0, TimeSpan.Zero), record.Bucket);
        Assert.Equal(51.876m, record.Cost);   // 311.256 × 10/60, kept raw: whole VND happens on the invoice line
    }

    // The record carries the tier's price, snapshotted. This is where price history lives, which is
    // why the offering can be repriced later without touching it.
    [Fact]
    public async Task RunTick_RecordCarriesTheTiersPrice()
    {
        _offerings.Seed(Offering());
        SeedRunningVms(RunningVm());

        await CreateService().RunTickAsync(Now, default);

        var record = Assert.Single(_usageRepo.All);
        Assert.Equal(Price, record.UnitPricePerHour.PerUnit);
        Assert.Equal(BillingConstants.DefaultCurrency, record.Currency);
    }

    // Repricing the tier does not reach back into a record already written for this bucket. The
    // snapshot is the whole mechanism.
    [Fact]
    public async Task RunTick_RepricingTierAfterTick_DoesNotChangeRecordedPrice()
    {
        var offering = Offering();
        _offerings.Seed(offering);
        SeedRunningVms(RunningVm());
        await CreateService().RunTickAsync(Now, default);

        offering.ChangePrice(999_999m);

        var record = Assert.Single(_usageRepo.All);
        Assert.Equal(Price, record.UnitPricePerHour.PerUnit);
    }

    // VMs not Running are never presented to the meter (the repository already filtered them
    // out), so the meter naturally records nothing for them.
    [Fact]
    public async Task RunTick_NoRunningVms_InsertsNothing()
    {
        _offerings.Seed(Offering());
        SeedRunningVms();

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(0, inserted);
        Assert.Empty(_usageRepo.All);
    }

    // Which VMs are billable (a qemu process alive: every Incus status but Stopped) is the repository's query,
    // measured on a real query in VmRepositoryStateTests.Billable_IsEveryStatusWithItsQemuAlive_NeverStoppedNorAbsent;
    // this meter bills exactly what that query returns.

    // https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/money-and-wallet.md#price-belongs-to-offering — a VM pointing at a tier that is not
    // in the catalog is damaged data, not a zero price: the meter refuses to bill rather than write a
    // silent 0. The failed tick is what makes /healthz/meters report it instead of it surfacing months
    // later as a hole in an invoice.
    [Fact]
    public async Task RunTick_OfferingMissing_ThrowsAndWritesNothing()
    {
        _offerings.Seed(Offering());
        SeedRunningVms(RunningVm("tier-that-was-deleted"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().RunTickAsync(Now, default));

        Assert.Contains("tier-that-was-deleted", ex.Message);
        Assert.Empty(_usageRepo.All);
    }

    // The refusal must be total, not per-VM: a half-billed tick would under-bill some tenants and
    // not others, with nothing recording which. The repository returns rows in no fixed order, so the
    // broken VM is put on each side of the healthy one (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/billing/metering/design.md#missing-offering).
    [Fact]
    public async Task RunTick_MissingOfferingBeforeAHealthyVm_WritesNothing()
    {
        _offerings.Seed(Offering());
        SeedRunningVms(RunningVm("tier-that-was-deleted"), RunningVm());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().RunTickAsync(Now, default));

        Assert.Empty(_usageRepo.All);
    }

    [Fact]
    public async Task RunTick_MissingOfferingAfterAHealthyVm_WritesNothing()
    {
        _offerings.Seed(Offering());
        SeedRunningVms(RunningVm(), RunningVm("tier-that-was-deleted"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateService().RunTickAsync(Now, default));

        Assert.Empty(_usageRepo.All);
    }

    // Two ticks landing in the same bucket must not double the record.
    [Fact]
    public async Task RunTick_CalledTwiceInSameBucket_InsertsOnlyOneRecord()
    {
        _offerings.Seed(Offering());
        SeedRunningVms(RunningVm());

        var first = await CreateService().RunTickAsync(Now, default);
        var second = await CreateService().RunTickAsync(Now.AddMinutes(3), default); // same 10-min bucket

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Single(_usageRepo.All);
    }

    [Fact]
    public async Task RunTick_MultipleRunningVms_OneRecordEach()
    {
        _offerings.Seed(Offering());
        SeedRunningVms(RunningVm(), RunningVm());

        var inserted = await CreateService().RunTickAsync(Now, default);

        Assert.Equal(2, inserted);
        Assert.Equal(2, _usageRepo.All.Count);
        Assert.Equal(2, _usageRepo.All.Select(r => r.ResourceId).Distinct().Count());
    }

    // The approved price table carries six decimal places. Rounding anywhere between the
    // catalog row and the usage record is a billing discrepancy, not a display glitch.
    [Fact]
    public async Task RunTick_PreservesSixDecimalPlacesFromCatalogToRecord()
    {
        _offerings.Seed(Offering(price: 161.388001m));
        SeedRunningVms(RunningVm());

        await CreateService().RunTickAsync(Now, default);

        Assert.Equal(161.388001m, Assert.Single(_usageRepo.All).UnitPricePerHour.PerUnit);
    }
}
