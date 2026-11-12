using FastEndpoints;
using Titan.Application.Queries.Billing.UsageRecord;
using Titan.Application.Queries.Billing.UsageRecord.Contracts;

namespace Titan.Infrastructure.Endpoints.Billing;

public class GetTenantBillingSummaryEndpoint(
    GetTenantBillingSummaryQueryHandler handler) : EndpointWithoutRequest<TenantBillingSummaryQueryResult>
{
    public override void Configure()
    {
        Get("v1/tenant/billing/summary");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var monthParam = Query<string>("month", isRequired: false) ?? $"{DateTime.UtcNow.Year}-{DateTime.UtcNow.Month:D2}";
        if (!TryParseYearMonth(monthParam, out var year, out var month))
            ThrowError("Invalid month format. Use YYYY-MM.", 400);

        // VmName comes from the UsageRecord snapshot, not the vms table: renaming a VM must not rewrite past
        // bills (GetTenantBillingSummaryEndpointTests.HandleAsync_ReturnsVmNameFromUsageSnapshot_NotFromVmsTable).
        await SendOkAsync(await handler.HandleAsync(new GetTenantBillingSummaryQuery(year, month), ct), ct);
    }

    private static bool TryParseYearMonth(string value, out int year, out int month)
    {
        year = 0; month = 0;
        var parts = value.Split('-');
        bool parsed = parts.Length == 2 && int.TryParse(parts[0], out year) && int.TryParse(parts[1], out month);
        return parsed && year >= 1 && year <= 9999 && month >= 1 && month <= 12;
    }
}
