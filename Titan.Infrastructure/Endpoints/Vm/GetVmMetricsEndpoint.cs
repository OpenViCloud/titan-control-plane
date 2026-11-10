using FastEndpoints;
using FluentValidation;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Vm;

// A bound request, not a read of the raw query string, so the spec declares range and generated clients carry it.
public record GetVmMetricsRequest([property: QueryParam] string? Range);

// titan-ops features/vm/design.md#vm-metrics: no range reads the last hour.
public class GetVmMetricsEndpoint(GetVmMetricsQueryHandler handler) : Endpoint<GetVmMetricsRequest, VmMetricsQueryResult>
{
    public override void Configure()
    {
        Get("v1/tenant/vms/{vmId:guid}/metrics");
    }

    public override async Task HandleAsync(GetVmMetricsRequest req, CancellationToken ct)
    {
        string range = VmMetricPoints.OneHour;
        if (req.Range != null)
        {
            range = req.Range;
        }
        Guid vmId = Route<Guid>("vmId");
        await SendAsync(await handler.HandleAsync(new GetVmMetricsQuery(vmId, range), ct), cancellation: ct);
    }
}

public class GetVmMetricsValidator : Validator<GetVmMetricsRequest>
{
    public GetVmMetricsValidator()
    {
        RuleFor(r => r.Range)
            .Must(r => r == null || VmMetricPoints.IsARange(r))
            .WithMessage("range must be 1h, 24h or 7d.");
    }
}
