using FastEndpoints;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Billing.UsageRecord;
using Titan.Application.Queries.Billing.UsageRecord.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Endpoints.Billing;

public class GetVmCostEndpoint(GetVmCostQueryHandler handler, ICurrentUser currentUser, IVmRepository vms)
    : EndpointWithoutRequest<VmCostQueryResult>
{
    public override void Configure()
    {
        Get("v1/tenant/vms/{vmId:guid}/cost");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");

        var vm = await vms.FindByIdAsync(vmId, ct);
        if (vm is null || vm.TenantId != currentUser.TenantId)
            throw new NotFoundException("VM_NOT_FOUND", $"VM {vmId} not found.");

        var monthParam = Query<string>("month", isRequired: false) ?? $"{DateTime.UtcNow.Year}-{DateTime.UtcNow.Month:D2}";
        if (!TryParseYearMonth(monthParam, out var year, out var month))
            ThrowError("Invalid month format. Use YYYY-MM.", 400);

        var result = await handler.HandleAsync(new GetVmCostQuery(vmId, year, month), ct);
        await SendOkAsync(result, ct);
    }

    private static bool TryParseYearMonth(string value, out int year, out int month)
    {
        year = 0; month = 0;
        var parts = value.Split('-');
        bool parsed = parts.Length == 2 && int.TryParse(parts[0], out year) && int.TryParse(parts[1], out month);
        return parsed && month >= 1 && month <= 12;
    }
}
