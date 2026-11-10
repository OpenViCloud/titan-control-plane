using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Repositories;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;

namespace Titan.Application.Queries.Compute.Vm;

// A tenant's VM usage over one range (titan-ops features/vm/design.md#vm-metrics). Another tenant's VM answers like a
// missing one. CPU percent is per vCPU of the VM's offering.
public class GetVmMetricsQueryHandler(
    IVmRepository vms,
    IVmOfferingRepository offerings,
    IVmMetricStore store,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public async Task<VmMetricsQueryResult> HandleAsync(GetVmMetricsQuery query, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        VmAggregate vm = await ComputeOwnership.RequireVmAsync(vms, tenantId, query.VmId, ct);

        int stepSeconds = VmMetricPoints.StepSecondsFor(query.Range);
        DateTimeOffset from = clock.GetUtcNow() - VmMetricPoints.LengthOf(query.Range);

        // The offering is the only place a VM's vCPU count lives; with none, CPU percent is null.
        VmOffering? offering = await offerings.FindByIdAsync(vm.VmOfferingId, ct);
        int vcpuCount = 0;
        if (offering != null)
        {
            vcpuCount = offering.VcpuCount;
        }

        List<VmMetricReading> readings = await store.ListFromAsync(vm.Id, from, ct);
        List<VmMetricPointQueryResult> points = VmMetricPoints.From(readings, from, vcpuCount, query.Range);
        return new VmMetricsQueryResult(query.Range, stepSeconds, points);
    }
}
