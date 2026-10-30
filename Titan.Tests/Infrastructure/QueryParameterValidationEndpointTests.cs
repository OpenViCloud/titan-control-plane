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
    private static readonly Guid Tenant = Guid.NewGuid();

    private static ICurrentUser Caller()
    {
        var caller = new Mock<ICurrentUser>();
        caller.Setup(u => u.TenantId).Returns(Tenant);
        return caller.Object;
    }

    private static GetTenantBillingSummaryEndpoint SummaryEndpoint(string month) =>
        Factory.Create<GetTenantBillingSummaryEndpoint>(
            ctx => ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?month={month}"),
            new GetTenantBillingSummaryQueryHandler(new FakeUsageRecordRepository(), Caller()));

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

    [Fact]
    public async Task BillingSummary_ValidMonth_IsAnswered()
    {
        GetTenantBillingSummaryEndpoint ep = SummaryEndpoint("2026-12");

        await ep.HandleAsync(CancellationToken.None);

        Assert.NotNull(ep.Response);
    }

    [Fact]
    public async Task VmCost_MonthOutsideOneToTwelve_Is400()
    {
        Vm vm = Vm.CreateUnchecked(Tenant, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        var vms = new Mock<IVmRepository>();
        vms.Setup(r => r.FindByIdAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        GetVmCostEndpoint ep = Factory.Create<GetVmCostEndpoint>(
            ctx =>
            {
                ctx.Request.RouteValues["vmId"] = vm.Id.ToString();
                ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString("?month=2026-13");
            },
            new GetVmCostQueryHandler(new FakeUsageRecordRepository(), Caller()), Caller(), vms.Object);

        await Assert.ThrowsAsync<ValidationFailureException>(() => ep.HandleAsync(CancellationToken.None));
    }

    private static ListTopUpsAdminEndpoint TopUpsEndpoint()
    {
        var repo = new Mock<ICreditRepository>();
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
