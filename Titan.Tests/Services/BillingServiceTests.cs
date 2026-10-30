using Titan.Domain.Values;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Services.Billing;
using Titan.Application.Queries.Billing.UsageRecord;
using Titan.Application.Queries.Billing.UsageRecord.Contracts;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

public class BillingServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid VmId = Guid.NewGuid();
    private const string VmName = "web-prod-1";
    private static readonly DateTimeOffset T0 = new(2026, 4, 10, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeUsageRecordRepository _usageRepo = new();
    private readonly Mock<ICurrentUser> _currentUser = new();

    public BillingServiceTests()
    {
        _currentUser.Setup(u => u.TenantId).Returns(TenantId);
        _currentUser.Setup(u => u.KeycloakUserId).Returns("keycloak-test-user");
    }

    // Facade over the two billing query handlers: every method forwards to exactly one handler and
    // adds nothing.
    private BillingFacade CreateService() => new(_usageRepo, _currentUser.Object);

    private sealed class BillingFacade(IUsageRecordRepository usageRepo, ICurrentUser currentUser)
    {
        public Task<VmCostQueryResult> GetVmCostAsync(Guid vmId, int year, int month, CancellationToken ct)
            => new GetVmCostQueryHandler(usageRepo, currentUser).HandleAsync(new GetVmCostQuery(vmId, year, month), ct);

        public Task<TenantBillingSummaryQueryResult> GetTenantSummaryAsync(int year, int month, CancellationToken ct)
            => new GetTenantBillingSummaryQueryHandler(usageRepo, currentUser)
                .HandleAsync(new GetTenantBillingSummaryQuery(year, month), ct);
    }

    // ── GetVmCost — compute-only ───────────────────────────

    [Fact]
    public async Task GetVmCost_ReturnsBreakdownForMonth()
    {
        var record = UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "SGD");
        _usageRepo.Seed(record);

        var result = await CreateService().GetVmCostAsync(VmId, 2026, 4, default);

        Assert.Equal(VmId, result.VmId);
        Assert.Equal("2026-04", result.Month);
        Assert.Equal(BillingConstants.DefaultCurrency, result.Currency);
        Assert.Single(result.Breakdown);
        Assert.True(result.TotalCost > 0);
    }

    [Fact]
    public async Task GetVmCost_NoUsageInMonth_ReturnsZero()
    {
        var result = await CreateService().GetVmCostAsync(VmId, 2026, 3, default);

        Assert.Equal(0m, result.TotalCost);
        Assert.Empty(result.Breakdown);
    }

    [Fact]
    public async Task GetVmCost_BreakdownSortedByBucketAsc()
    {
        var r1 = UsageRecord.CreateCompute(VmId, VmName, TenantId, T0.AddDays(5), 10, Rate.Of(6000m), "SGD");
        var r2 = UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "SGD");
        _usageRepo.Seed(r1, r2);

        var result = await CreateService().GetVmCostAsync(VmId, 2026, 4, default);

        Assert.Equal(2, result.Breakdown.Count);
        Assert.True(result.Breakdown[0].Bucket <= result.Breakdown[1].Bucket);
    }

    [Fact]
    public async Task GetVmCost_NoTenant_ThrowsNotAMember()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateService().GetVmCostAsync(VmId, 2026, 4, default));
        Assert.Equal("NOT_A_MEMBER", ex.Code);
    }

    // A DISK record sharing the VM's id (a volume id equal to a vm id is possible in
    // principle; the guard must be on ResourceType, not just on matching the id) must never leak
    // into a VM's compute-only cost.
    [Fact]
    public async Task GetVmCost_IgnoresDiskRecordsEvenWithMatchingResourceId()
    {
        _usageRepo.Seed(
            UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "SGD"),
            UsageRecord.CreateDisk(VmId, "collides-with-vm-id", TenantId, T0, 10, 40, Rate.Of(300m), "SGD"));

        var result = await CreateService().GetVmCostAsync(VmId, 2026, 4, default);

        var record = Assert.Single(result.Breakdown);
        Assert.Equal("RUNNING_VM", record.UsageType);
    }

    // ── GetTenantSummary ──────────────────────────────────────────────────

    [Fact]
    public async Task GetTenantSummary_AggregatesTotalAcrossVms()
    {
        var vmA = Guid.NewGuid();
        var vmB = Guid.NewGuid();
        _usageRepo.Seed(
            UsageRecord.CreateCompute(vmA, "api-server", TenantId, T0, 10, Rate.Of(6000m), "SGD"),
            UsageRecord.CreateCompute(vmB, "db-primary", TenantId, T0, 10, Rate.Of(12000m), "SGD"));

        var result = await CreateService().GetTenantSummaryAsync(2026, 4, default);

        Assert.Equal("2026-04", result.Month);
        Assert.Equal(BillingConstants.DefaultCurrency, result.Currency);
        Assert.Equal(2, result.PerVm.Count);
        Assert.True(result.TotalCost > 0);
        Assert.Equal(result.PerVm.Sum(v => v.TotalCost), result.TotalCost);
    }

    [Fact]
    public async Task GetTenantSummary_PerVmSortedByCostDesc()
    {
        var cheap = Guid.NewGuid();
        var expensive = Guid.NewGuid();
        _usageRepo.Seed(
            UsageRecord.CreateCompute(cheap, "small-vm", TenantId, T0, 10, Rate.Of(6000m), "SGD"),
            UsageRecord.CreateCompute(expensive, "large-vm", TenantId, T0, 10, Rate.Of(18000m), "SGD"));

        var result = await CreateService().GetTenantSummaryAsync(2026, 4, default);

        Assert.True(result.PerVm[0].TotalCost >= result.PerVm[1].TotalCost);
    }

    [Fact]
    public async Task GetTenantSummary_VmNameIsSnapshotName_NotUuid()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "SGD"));

        var result = await CreateService().GetTenantSummaryAsync(2026, 4, default);

        Assert.Equal(VmName, result.PerVm[0].VmName);
    }

    [Fact]
    public async Task GetTenantSummary_NoUsage_ReturnsZero()
    {
        var result = await CreateService().GetTenantSummaryAsync(2026, 3, default);

        Assert.Equal(0m, result.TotalCost);
        Assert.Empty(result.PerVm);
        // An empty period is normal, not a defect: it still reports a currency and must not throw, or
        // the billing page 500s.
        Assert.Equal(BillingConstants.DefaultCurrency, result.Currency);
    }

    [Fact]
    public async Task GetTenantSummary_NoTenant_ThrowsNotAMember()
    {
        _currentUser.Setup(u => u.TenantId).Returns((Guid?)null);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(
            () => CreateService().GetTenantSummaryAsync(2026, 4, default));
        Assert.Equal("NOT_A_MEMBER", ex.Code);
    }

    // Compute and Storage must add up to the total.
    [Fact]
    public async Task GetTenantSummary_SeparatesComputeAndStorageTotals()
    {
        var volumeId = Guid.NewGuid();
        _usageRepo.Seed(
            UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "SGD"),
            UsageRecord.CreateDisk(volumeId, "data-vol", TenantId, T0, 10, 40, Rate.Of(300m), "SGD"));

        var result = await CreateService().GetTenantSummaryAsync(2026, 4, default);

        Assert.Equal(1000m, result.ComputeCost);   // 6000 VND/h × 10/60 h = 1000 (titan-ops architecture/money-and-wallet.md#money-is-long-vnd, whole VND)
        Assert.Equal(2000m, result.StorageCost);    // 300 VND/GB/h × 10/60 h × 40 GB = 2000
        Assert.Equal(result.ComputeCost + result.StorageCost, result.TotalCost);
        // Disk usage does not attribute to a per-VM row — it is not owned by any one VM.
        Assert.Single(result.PerVm);
    }

    // GenerateInvoicesForPeriodCommandHandler bills every record grouped by (ResourceType, ResourceId),
    // so a SNAPSHOT record is charged as soon as the meter writes it. The summary allow-lists resource
    // types; without Snapshot in StorageCost the dashboard total would undercut the invoice.
    [Fact]
    public async Task GetTenantSummary_CountsSnapshotUsageAsStorage_SoTotalMatchesInvoice()
    {
        var snapshotId = Guid.NewGuid();
        _usageRepo.Seed(
            UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "SGD"),
            UsageRecord.CreateDisk(Guid.NewGuid(), "data-vol", TenantId, T0, 10, 40, Rate.Of(300m), "SGD"),
            UsageRecord.CreateSnapshot(snapshotId, "nightly-snap", TenantId, T0, 10, Rate.Of(6000m), "SGD"));

        var result = await CreateService().GetTenantSummaryAsync(2026, 4, default);

        var diskCost = 2000m;       // 300 VND/GB/h × 10/60 h × 40 GB
        var snapshotCost = 1000m;   // 6000 VND/h × 10/60 h (flat per-snapshot)
        Assert.Equal(diskCost + snapshotCost, result.StorageCost);
        // The invariant that actually protects the customer: nothing billable is dropped.
        Assert.Equal(1000m /* compute */ + diskCost + snapshotCost, result.TotalCost);
        // A snapshot is not a VM — it must not appear as a per-VM row.
        Assert.Single(result.PerVm);
        Assert.DoesNotContain(result.PerVm, v => v.VmId == snapshotId);
    }

    // The summary and the VM cost read the raw product; rounding belongs to the invoice line
    // (titan-ops architecture/money-and-wallet.md#money-is-long-vnd).
    [Fact]
    public async Task GetTenantSummary_AndVmCost_AreRawSums_NotRoundedPerRecord()
    {
        _usageRepo.Seed(
            UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(1.57914m), "SGD"),
            UsageRecord.CreateCompute(VmId, VmName, TenantId, T0.AddMinutes(10), 10, Rate.Of(1.57914m), "SGD"),
            UsageRecord.CreateDisk(Guid.NewGuid(), "data-vol", TenantId, T0, 10, 1, Rate.Of(1.57914m), "SGD"),
            UsageRecord.CreateSnapshot(Guid.NewGuid(), "nightly-snap", TenantId, T0, 10, Rate.Of(1.8m), "SGD"));

        var summary = await CreateService().GetTenantSummaryAsync(2026, 4, default);
        var vmCost = await CreateService().GetVmCostAsync(VmId, 2026, 4, default);

        Assert.Equal(0.52638m, summary.ComputeCost);   // 2 × 1.57914 × 10/60
        Assert.Equal(0.56319m, summary.StorageCost);   // 0.26319 disk + 0.3 snapshot
        Assert.Equal(0.52638m, vmCost.TotalCost);
    }

    // ── Currency ─────────────────────────────────────────────────
    // The read path reports the CONFIGURED currency and ignores the `Currency` column on the rows it
    // sums; a resolve-from-the-rows rule turns both red.
    [Fact]
    public async Task GetTenantSummary_ReportsConfiguredCurrency_IgnoringRecordedValue()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "USD"));

        var result = await CreateService().GetTenantSummaryAsync(2026, 4, default);

        Assert.Equal(BillingConstants.DefaultCurrency, result.Currency);
    }

    [Fact]
    public async Task GetVmCost_ReportsConfiguredCurrency_IgnoringRecordedValue()
    {
        _usageRepo.Seed(UsageRecord.CreateCompute(VmId, VmName, TenantId, T0, 10, Rate.Of(6000m), "USD"));

        var result = await CreateService().GetVmCostAsync(VmId, 2026, 4, default);

        Assert.Equal(BillingConstants.DefaultCurrency, result.Currency);
    }

}

// ── Fakes ─────────────────────────────────────────────────────────────────

public class FakeUsageRecordRepository : IUsageRecordRepository
{
    public List<UsageRecord> All { get; } = [];

    public void Seed(params UsageRecord[] records) => All.AddRange(records);

    // Simulates the DB's UNIQUE(resource_type, resource_id, bucket) + ON CONFLICT DO NOTHING: a
    // second insert for the same key is a no-op, not an error, and reports false — mirroring
    // UsageRecordRepository.TryInsertAsync's real-database behavior without needing Postgres (the
    // index itself is pinned by UsageRecordSchemaTests).
    public Task<bool> TryInsertAsync(UsageRecord record, CancellationToken ct)
    {
        var exists = All.Any(r =>
            r.ResourceType == record.ResourceType &&
            r.ResourceId == record.ResourceId &&
            r.Bucket == record.Bucket);

        if (exists)
            return Task.FromResult(false);

        All.Add(record);
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<UsageRecord>> GetByResourceAndMonthAsync(
        UsageResourceType resourceType, Guid resourceId, int year, int month, CancellationToken ct)
    {
        var result = All
            .Where(r => r.ResourceType == resourceType && r.ResourceId == resourceId
                        && r.Bucket.Year == year && r.Bucket.Month == month)
            .OrderBy(r => r.Bucket)
            .ToList();
        return Task.FromResult<IReadOnlyList<UsageRecord>>(result);
    }

    public Task<IReadOnlyList<UsageRecord>> GetByTenantAndMonthAsync(Guid tenantId, int year, int month, CancellationToken ct)
    {
        var result = All
            .Where(r => r.TenantId == tenantId && r.Bucket.Year == year && r.Bucket.Month == month)
            .ToList();
        return Task.FromResult<IReadOnlyList<UsageRecord>>(result);
    }

    public Task<IReadOnlyList<Guid>> GetDistinctTenantIdsForMonthAsync(int year, int month, CancellationToken ct)
    {
        var result = All
            .Where(r => r.Bucket.Year == year && r.Bucket.Month == month)
            .Select(r => r.TenantId)
            .Distinct()
            .ToList();
        return Task.FromResult<IReadOnlyList<Guid>>(result);
    }
}

/// Catalog fakes for the meters: price lives on the offering
/// (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering), so "what does this tier cost" is a
/// catalog read.
public class FakeVmOfferingRepository : IVmOfferingRepository
{
    public List<Titan.Domain.Aggregates.VmOffering.VmOffering> All { get; } = [];
    public int SaveCount { get; private set; }

    public void Seed(params Titan.Domain.Aggregates.VmOffering.VmOffering[] offerings) => All.AddRange(offerings);

    public Task<Titan.Domain.Aggregates.VmOffering.VmOffering?> FindByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(All.FirstOrDefault(o => o.Id == id));

    public Task<List<Titan.Domain.Aggregates.VmOffering.VmOffering>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult(All.ToList());

    public Task SaveAsync(CancellationToken ct = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

public class FakeVolumeOfferingRepository : IVolumeOfferingRepository
{
    public List<Titan.Domain.Aggregates.VolumeOffering.VolumeOffering> All { get; } = [];

    public void Seed(params Titan.Domain.Aggregates.VolumeOffering.VolumeOffering[] offerings) => All.AddRange(offerings);

    public Task<Titan.Domain.Aggregates.VolumeOffering.VolumeOffering?> FindByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(All.FirstOrDefault(o => o.Id == id));

    public Task<List<Titan.Domain.Aggregates.VolumeOffering.VolumeOffering>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult(All.ToList());
}

public class FakeSnapshotOfferingRepository : ISnapshotOfferingRepository
{
    public List<Titan.Domain.Aggregates.SnapshotOffering.SnapshotOffering> All { get; } = [];

    public void Seed(params Titan.Domain.Aggregates.SnapshotOffering.SnapshotOffering[] offerings) => All.AddRange(offerings);

    public Task<Titan.Domain.Aggregates.SnapshotOffering.SnapshotOffering?> FindByIdAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(All.FirstOrDefault(o => o.Id == id));

    public Task<List<Titan.Domain.Aggregates.SnapshotOffering.SnapshotOffering>> ListAllAsync(CancellationToken ct = default) =>
        Task.FromResult(All.ToList());
}
