using VmOfferingAggregate = Titan.Domain.Aggregates.VmOffering.VmOffering;

namespace Titan.Application.Queries.Catalog.VmOffering.Contracts;

public record VmOfferingQueryResult(
    string Id,
    string Name,
    int Vcpu,           // = VcpuCount; the worker sizes the instance from it (limits.cpu)
    int MemoryMb,
    // VND/hour: one currency system-wide, so no per-row currency (titan-ops architecture/money-and-wallet.md#money-is-long-vnd).
    // Public so a customer sees what a tier costs before creating one
    // (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
    decimal PricePerHour)
{
    public static VmOfferingQueryResult From(VmOfferingAggregate o) =>
        new(o.Id, o.Name, o.VcpuCount, o.MemoryMb, o.PricePerHour.PerUnit);
}
