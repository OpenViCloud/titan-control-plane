using FastEndpoints;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Billing.TopUp;
using Titan.Application.Queries.Billing.UsageRecord;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Vm;
using TopUpAggregate = Titan.Domain.Aggregates.TopUp.TopUp;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Endpoints.Billing;
using Titan.Infrastructure.Endpoints.TopUp;
using Titan.Tests.Services;

namespace Titan.Tests.Infrastructure;

// A query parameter that parses but names nothing (month 13, a source number no enum member has) must be a 400,
// not a 200 with an empty body that reads like "no usage" or "no top-ups".
public class QueryParameterValidationEndpointTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid Tenant = TenantOwner.Id;

    private static ICurrentUser Caller()
    {
        var caller = new Mock<ICurrentUser>();
        caller.Setup(u => u.TenantId).Returns(Tenant);
        return caller.Object;
    }

    private static GetTenantBillingSummaryEndpoint SummaryEndpoint(string month) =>
        Factory.Create<GetTenantBillingSummaryEndpoint>(
            ctx => ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?month={month}"),
            new GetTenantBillingSummaryQueryHandler(new FakeUsageRecordRepository(), Caller(), TimeProvider.System));

    // "abc" guards the error path itself: a month that is not a number must come back as a 400, not as an
    // exception from an overload that needs a request object this endpoint does not have.
    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("abc")]
    public async Task BillingSummary_MonthOutsideOneToTwelve_Is400(string month)
    {
        GetTenantBillingSummaryEndpoint ep = SummaryEndpoint(month);

        await Assert.ThrowsAsync<ValidationFailureException>(() => ep.HandleAsync(CancellationToken.None));
    }

    // The current month and the last year a date can carry: both kept, both answered.
    [Theory]
    [InlineData(null)]
    [InlineData("9999-12")]
    public async Task BillingSummary_ValidMonth_IsAnswered(string? month)
    {
        GetTenantBillingSummaryEndpoint ep = SummaryEndpoint(month ?? DateTime.UtcNow.ToString("yyyy-MM"));

        await ep.HandleAsync(CancellationToken.None);

        Assert.NotNull(ep.Response);
    }

    [Fact]
    public async Task VmCost_MonthOutsideOneToTwelve_Is400()
    {
        Vm vm = Vm.Create(TenantOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        var vms = new Mock<IVmRepository>();
        vms.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        GetVmCostEndpoint ep = Factory.Create<GetVmCostEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["vmId"] = vm.Id.ToString();
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?month=2026-13");
            },
            new GetVmCostQueryHandler(new FakeUsageRecordRepository(), Caller(), TimeProvider.System), Caller(), vms.Object);

        await Assert.ThrowsAsync<ValidationFailureException>(() => ep.HandleAsync(CancellationToken.None));
    }

    // A year no date can carry must be a 400 before the handler: the retention check builds the month's first day.
    [Theory]
    [InlineData("0-05")]
    [InlineData("10000-01")]
    public async Task BillingSummary_YearOutsideOneTo9999_Is400(string month)
    {
        GetTenantBillingSummaryEndpoint ep = SummaryEndpoint(month);

        await Assert.ThrowsAsync<ValidationFailureException>(() => ep.HandleAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("0-05")]
    [InlineData("10000-01")]
    public async Task VmCost_YearOutsideOneTo9999_Is400(string month)
    {
        Vm vm = Vm.Create(TenantOwner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        var vms = new Mock<IVmRepository>();
        vms.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        GetVmCostEndpoint ep = Factory.Create<GetVmCostEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["vmId"] = vm.Id.ToString();
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?month={month}");
            },
            new GetVmCostQueryHandler(new FakeUsageRecordRepository(), Caller(), TimeProvider.System), Caller(), vms.Object);

        await Assert.ThrowsAsync<ValidationFailureException>(() => ep.HandleAsync(CancellationToken.None));
    }

    // A month whose first day is more than 90 days back is no longer whole in the ledger: 422, never a 200 that reads
    // like "nothing was used" (titan-ops features/billing/metering/design.md#usage-retention).
    [Fact]
    public async Task BillingSummary_AMonthOlderThanTheLedgerKeeps_IsUsagePeriodExpired()
    {
        GetTenantBillingSummaryEndpoint ep = SummaryEndpoint("2020-01");

        var e = await Assert.ThrowsAsync<Titan.Domain.Exceptions.BusinessException>(() => ep.HandleAsync(CancellationToken.None));

        Assert.Equal("USAGE_PERIOD_EXPIRED", e.Code);
    }

    // The VM is checked before the month: an expired month never tells a caller that a foreign VM exists.
    [Fact]
    public async Task VmCost_AForeignVm_IsNotFound_EvenForAnExpiredMonth()
    {
        Vm foreign = Vm.Create(Guid.NewGuid(), Guid.NewGuid(), "vm-x", "m", "tpl-1", "cluster-a", 20);
        var vms = new Mock<IVmRepository>();
        vms.Setup(r => r.FindByIdAsync(foreign.Id, It.IsAny<CancellationToken>())).ReturnsAsync(foreign);
        GetVmCostEndpoint ep = Factory.Create<GetVmCostEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["vmId"] = foreign.Id.ToString();
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?month=2020-01");
            },
            new GetVmCostQueryHandler(new FakeUsageRecordRepository(), Caller(), TimeProvider.System), Caller(), vms.Object);

        var e = await Assert.ThrowsAsync<Titan.Domain.Exceptions.NotFoundException>(() => ep.HandleAsync(CancellationToken.None));

        Assert.Equal("VM_NOT_FOUND", e.Code);
    }

    private static ListTopUpsAdminEndpoint TopUpsEndpoint()
    {
        var repo = new Mock<ITopUpRepository>();
        repo.Setup(r => r.ListTopUpsAsync(It.IsAny<TopUpSource?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<TopUpAggregate>());
        return Factory.Create<ListTopUpsAdminEndpoint>(new ListTopUpsQueryHandler(repo.Object));
    }

    [Fact]
    public async Task AdminTopUps_SourceNumberThatIsNoMember_Is400()
    {
        ListTopUpsAdminEndpoint ep = TopUpsEndpoint();

        await ep.HandleAsync(new ListTopUpsAdminRequest("9"), CancellationToken.None);

        Assert.Equal(400, ep.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task AdminTopUps_SourceByName_IsAnswered()
    {
        ListTopUpsAdminEndpoint ep = TopUpsEndpoint();

        await ep.HandleAsync(new ListTopUpsAdminRequest("PromotionalCredit"), CancellationToken.None);

        Assert.NotEqual(400, ep.HttpContext.Response.StatusCode);
    }
}
