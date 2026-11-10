using VolumeOfferingAggregate = Titan.Domain.Aggregates.VolumeOffering.VolumeOffering;

namespace Titan.Application.Queries.Catalog.VolumeOffering.Contracts;

// Carries the tier's own price (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
public record VolumeOfferingQueryResult(string Id, string Name, decimal PricePerGbPerHour)
{
    public static VolumeOfferingQueryResult From(VolumeOfferingAggregate o) =>
        new(o.Id, o.Name, o.PricePerGbPerHour.PerUnit);
}
