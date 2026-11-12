using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Billing.Invoice;
using Titan.Application.Commands.Billing.Invoice.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

public class RunInvoiceGenerationRequest
{
    public int? Year { get; set; }
    public int? Month { get; set; }
}

public class RunInvoiceGenerationResponse
{
    public int Generated { get; set; }
    public int Skipped { get; set; }
}

/// Triggered by the gitops CronJob on the 1st of every month. Re-running for an already-billed period is safe
/// (one invoice per tenant per period), so the CronJob needs no idempotency of its own. A period whose first day is
/// more than 90 days back is refused with 422 USAGE_PERIOD_EXPIRED (titan-ops features/billing/metering/design.md#usage-retention).
public class RunInvoiceGenerationEndpoint(GenerateInvoicesForPeriodCommandHandler handler, IConfiguration config)
    : Endpoint<RunInvoiceGenerationRequest, RunInvoiceGenerationResponse>
{
    public override void Configure()
    {
        Post("internal/billing/invoices/run");
        AllowAnonymous();
    }

    public override async Task HandleAsync(RunInvoiceGenerationRequest req, CancellationToken ct)
    {
        var token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        var (year, month) = ResolvePeriod(req.Year, req.Month);
        var result = await handler.HandleAsync(new GenerateInvoicesForPeriodCommand(year, month), ct);

        await SendOkAsync(new RunInvoiceGenerationResponse
        {
            Generated = result.Generated,
            Skipped = result.Skipped
        }, ct);
    }

    /// A missing year or month defaults both to the previous UTC calendar month: the period that just closed.
    private static (int Year, int Month) ResolvePeriod(int? year, int? month)
    {
        if (year is not null && month is not null)
            return (year.Value, month.Value);

        var previousMonth = DateTime.UtcNow.AddMonths(-1);
        return (previousMonth.Year, previousMonth.Month);
    }
}
