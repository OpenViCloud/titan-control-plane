using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Catalog.SnapshotOffering;

// Not a choice a customer makes — a snapshot carries no offering id — but it exists so that price lives on
// an offering with no exception (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering). One row,
// well-known id. Per snapshot per hour, not per GB: Titan tracks no snapshot size.
public class SnapshotOffering : AggregateRoot
{
    /// The meter resolves this constant; a missing row is a system fault, not a zero price.
    public const string DefaultId = "standard";

    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    /// VND per snapshot per hour (titan-ops architecture/money-and-wallet.md#rate-value-object).
    public Rate PricePerHour { get; private set; } = default!;

    private SnapshotOffering() { }

    public static SnapshotOffering Create(string id, string name, decimal pricePerHour)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must not be empty", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty", nameof(name));
        ValidatePrice(pricePerHour);

        return new SnapshotOffering { Id = id, Name = name, PricePerHour = Rate.Of(pricePerHour) };
    }

    public void ChangePrice(decimal pricePerHour)
    {
        ValidatePrice(pricePerHour);
        PricePerHour = Rate.Of(pricePerHour);
    }

    // Zero is allowed here and nowhere else in the catalog: it switches snapshot billing off.
    // A VM or volume tier at zero would be a mistake; a snapshot rate at zero is a decision.
    private static void ValidatePrice(decimal pricePerHour)
    {
        if (pricePerHour < 0)
            throw new ArgumentException("pricePerHour must be >= 0", nameof(pricePerHour));
    }
}
