using FastEndpoints;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Billing.UsageRecord;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Endpoints.Billing;
using Titan.Tests.Services;

namespace Titan.Tests.Infrastructure;

// The name on a bill is UsageRecord.ResourceName, captured when the usage was metered: billing reports the
// name as of the billing period and does not reach into the Vm aggregate
// (titan-ops architecture/code-structure.md#module-boundary). Re-fetching each VM name from the vms table inside
// Task.WhenAll means N concurrent EF queries on one scoped DbContext, which EF's ConcurrencyDetector rejects:
// a 500 for any tenant with two or more billed VMs. If the endpoint never consults IVmRepository, that
// cannot happen.
//
// The endpoint takes a concrete GetTenantBillingSummaryQueryHandler (no interface to stub), so these run
// the real handler over the in-memory usage ledger.
public class GetTenantBillingSummaryEndpointTests
{
    private const string Month = "2026-08";
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly DateTimeOffset InAugust = new(2026, 8, 10, 10, 0, 0, TimeSpan.Zero);

    // web-01 costs twice what db-01 does, so the per-VM order (most expensive first) is web-01, db-01.
    private static UsageRecord[] UsageWithTwoVms() =>
    [
        UsageRecord.CreateCompute(Guid.NewGuid(), "web-01", Tenant, InAugust, 60, Rate.Of(20_000m), "VND"),
        UsageRecord.CreateCompute(Guid.NewGuid(), "db-01", Tenant, InAugust, 60, Rate.Of(10_000m), "VND"),
    ];

    private static GetTenantBillingSummaryEndpoint CreateEndpoint(params UsageRecord[] usage)
    {
        var ledger = new FakeUsageRecordRepository();
        ledger.Seed(usage);
        var caller = new Mock<ICurrentUser>();
        caller.Setup(u => u.TenantId).Returns(Tenant);

        return Factory.Create<GetTenantBillingSummaryEndpoint>(
            ctx => ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?month={Month}"),
            new GetTenantBillingSummaryQueryHandler(ledger, caller.Object));
    }

    // The name shown on a bill is the name the VM had while it was accruing cost, taken
    // from the usage snapshot. Renaming a VM afterwards must not rewrite past bills.
    [Fact]
    public async Task HandleAsync_ReturnsVmNameFromUsageSnapshot_NotFromVmsTable()
    {
        var ep = CreateEndpoint(UsageWithTwoVms());

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(["web-01", "db-01"], ep.Response.PerVm.Select(v => v.VmName));
    }

    // One EF query per VM, all in flight at once on the request's single scoped DbContext, is a 500.
    // Reproducing that by timing would be flaky and would not stop the pattern returning, so the guard is
    // the dependency itself: this endpoint must not be able to reach the vms table. Re-adding
    // IVmRepository to the constructor fails here.
    [Fact]
    public void Endpoint_DoesNotDependOnVmRepository()
    {
        var dependencies = typeof(GetTenantBillingSummaryEndpoint)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);

        Assert.DoesNotContain(typeof(IVmRepository), dependencies);
    }

    // The headline total must equal what the per-VM rows add up to, or the page argues
    // with itself.
    [Fact]
    public async Task HandleAsync_TotalEqualsSumOfPerVmCosts()
    {
        var ep = CreateEndpoint(UsageWithTwoVms());

        await ep.HandleAsync(CancellationToken.None);

        Assert.True(ep.Response.TotalCost > 0);   // not vacuously 0 == 0
        Assert.Equal(ep.Response.PerVm.Sum(v => v.TotalCost), ep.Response.TotalCost);
    }

    // A tenant that has not been billed yet is a normal state, not an error.
    [Fact]
    public async Task HandleAsync_TenantWithNoUsage_ReturnsEmptySummary()
    {
        var ep = CreateEndpoint();

        await ep.HandleAsync(CancellationToken.None);

        Assert.Empty(ep.Response.PerVm);
        Assert.Equal(0m, ep.Response.TotalCost);
    }
}
