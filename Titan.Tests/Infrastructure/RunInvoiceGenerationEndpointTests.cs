using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Moq;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Tests.Services;

namespace Titan.Tests.Infrastructure;

// The monthly gitops CronJob is the only caller of this endpoint, authenticated by the shared
// X-Internal-Token like every other internal/* callback. This endpoint parses the request body and
// computes "previous month" itself, so it is not a thin forwarder and gets its own test.
//
// The endpoint takes a concrete GenerateInvoicesForPeriodCommandHandler (nothing to mock), so the period
// it asked for is read off the usage ledger — the first thing the handler queries with it.
public class RunInvoiceGenerationEndpointTests
{
    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private readonly Mock<IUsageRecordRepository> _usage = new();

    public RunInvoiceGenerationEndpointTests() =>
        _usage.Setup(u => u.GetDistinctTenantIdsForMonthAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

    private GenerateInvoicesForPeriodCommandHandler Handler() => new(new FakeInvoiceRepository(), _usage.Object);

    private void VerifyNeverGenerated() =>
        _usage.Verify(u => u.GetDistinctTenantIdsForMonthAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task HandleAsync_MissingTokenHeader_Returns401_AndNeverCallsService()
    {
        var ep = Factory.Create<RunInvoiceGenerationEndpoint>(
            _ => { /* no X-Internal-Token header */ },
            Handler(), ConfigWithToken("secret"));

        await ep.HandleAsync(new RunInvoiceGenerationRequest(), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        VerifyNeverGenerated();
    }

    [Fact]
    public async Task HandleAsync_WrongTokenHeader_Returns401_AndNeverCallsService()
    {
        var ep = Factory.Create<RunInvoiceGenerationEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "wrong",
            Handler(), ConfigWithToken("secret"));

        await ep.HandleAsync(new RunInvoiceGenerationRequest(), CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        VerifyNeverGenerated();
    }

    // A body omitting year/month defaults to the previous UTC calendar month, the period the
    // CronJob calls this the day after it closes.
    [Fact]
    public async Task HandleAsync_NoYearMonth_DefaultsToPreviousUtcMonth()
    {
        var ep = Factory.Create<RunInvoiceGenerationEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "secret",
            Handler(), ConfigWithToken("secret"));

        await ep.HandleAsync(new RunInvoiceGenerationRequest(), CancellationToken.None);

        var expected = DateTime.UtcNow.AddMonths(-1);
        _usage.Verify(u => u.GetDistinctTenantIdsForMonthAsync(
            expected.Year, expected.Month, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(200, ep.HttpContext.Response.StatusCode);
    }

    // An explicit period forwards exactly what was asked, not the default.
    [Fact]
    public async Task HandleAsync_WithYearMonth_ForwardsExactArgsToService()
    {
        var ep = Factory.Create<RunInvoiceGenerationEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "secret",
            Handler(), ConfigWithToken("secret"));

        await ep.HandleAsync(new RunInvoiceGenerationRequest { Year = 2026, Month = 7 }, CancellationToken.None);

        _usage.Verify(u => u.GetDistinctTenantIdsForMonthAsync(2026, 7, It.IsAny<CancellationToken>()), Times.Once);
    }

    // The counts the CronJob logs are the handler's own, carried through unchanged: two tenants billed,
    // one already billed for the period and skipped.
    [Fact]
    public async Task HandleAsync_ReportsGeneratedAndSkipped_FromTheRun()
    {
        var july = new DateTimeOffset(2026, 7, 10, 10, 0, 0, TimeSpan.Zero);
        var (a, b, billed) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var ledger = new FakeUsageRecordRepository();
        ledger.Seed(
            UsageRecord.CreateCompute(Guid.NewGuid(), "vm-a", a, july, 60, Rate.Of(1000m), "VND"),
            UsageRecord.CreateCompute(Guid.NewGuid(), "vm-b", b, july, 60, Rate.Of(1000m), "VND"),
            UsageRecord.CreateCompute(Guid.NewGuid(), "vm-c", billed, july, 60, Rate.Of(1000m), "VND"));
        var invoices = new FakeInvoiceRepository();
        invoices.Seed(Invoice.Generate(billed, 2026, 7, july,
            [InvoiceItem.Create(UsageResourceType.Compute, Guid.NewGuid(), "vm-c", 1000L, "VND")], "VND"));

        var ep = Factory.Create<RunInvoiceGenerationEndpoint>(
            ctx => ctx.Request.Headers["X-Internal-Token"] = "secret",
            new GenerateInvoicesForPeriodCommandHandler(invoices, ledger), ConfigWithToken("secret"));

        await ep.HandleAsync(new RunInvoiceGenerationRequest { Year = 2026, Month = 7 }, CancellationToken.None);

        Assert.Equal(2, ep.Response.Generated);
        Assert.Equal(1, ep.Response.Skipped);
    }
}
