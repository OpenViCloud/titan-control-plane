namespace Titan.Application.Queries.Compute.Vm.Contracts;

/// Range is "1h", "24h" or "7d" (VmMetricPoints).
public record GetVmMetricsQuery(Guid VmId, string Range);
