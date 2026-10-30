using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Catalog.VolumeOffering;

// Titan owns this catalog; it is not read from the provider: storage placement is a worker concern.
// The tier carries its own per-GB price
// (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
//
// IsCustomized = true means the customer supplies the size at create time; SizeGb is then unused.
public class VolumeOffering : AggregateRoot
{
    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public int SizeGb { get; private set; }
    public bool IsCustomized { get; private set; }

    /// VND per GB per hour (titan-ops architecture/money-and-wallet.md#rate-value-object).
    public Rate PricePerGbPerHour { get; private set; } = default!;

    private VolumeOffering() { }

    public static VolumeOffering Create(
        string id, string name, int sizeGb, bool isCustomized, decimal pricePerGbPerHour)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must not be empty", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty", nameof(name));
        if (sizeGb < 0) throw new ArgumentException("sizeGb must be >= 0", nameof(sizeGb));
        // A fixed tier is defined by its size; advertising 0 GB would let a customer create a
        // zero-byte volume with no other guard to catch it.
        if (!isCustomized && sizeGb <= 0)
            throw new ArgumentException("a fixed-size offering must declare sizeGb > 0", nameof(sizeGb));
        if (pricePerGbPerHour <= 0)
            throw new ArgumentException("pricePerGbPerHour must be > 0", nameof(pricePerGbPerHour));

        return new VolumeOffering
        {
            Id = id,
            Name = name,
            SizeGb = sizeGb,
            IsCustomized = isCustomized,
            PricePerGbPerHour = Rate.Of(pricePerGbPerHour)
        };
    }
}
