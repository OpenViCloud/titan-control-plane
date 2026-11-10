using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.VolumeOffering;

// Titan owns this catalog; it is not read from the provider: storage placement is a worker concern.
// The tier carries its own per-GB price
// (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
//
// The customer supplies the size at create time; a tier is a name and a price.
public class VolumeOffering : IAggregateRoot
{
    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;

    /// VND per GB per hour (titan-ops architecture/money-and-wallet.md#rate-value-object).
    public Rate PricePerGbPerHour { get; private set; } = default!;

    private VolumeOffering() { }

    public static VolumeOffering Create(string id, string name, decimal pricePerGbPerHour)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must not be empty", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty", nameof(name));
        if (pricePerGbPerHour <= 0)
            throw new ArgumentException("pricePerGbPerHour must be > 0", nameof(pricePerGbPerHour));

        return new VolumeOffering
        {
            Id = id,
            Name = name,
            PricePerGbPerHour = Rate.Of(pricePerGbPerHour)
        };
    }
}
