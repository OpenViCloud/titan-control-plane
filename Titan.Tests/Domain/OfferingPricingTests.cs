using Titan.Domain.Aggregates.SnapshotOffering;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Aggregates.VolumeOffering;

namespace Titan.Tests.Domain;

/// titan-ops architecture/money-and-wallet.md#price-belongs-to-offering — price is an attribute of the product. A tier
/// that exists but has no price is unreachable, so a running VM cannot silently bill zero for lack of a price.
public class OfferingPricingTests
{
    // ── VmOffering ──────────────────────────────────────────────────

    [Fact]
    public void VmOffering_CarriesItsOwnPrice()
    {
        var offering = VmOffering.Create("m", "Medium", 1.0m, 2, 4096, pricePerHour: 311.256m);

        Assert.Equal(311.256m, offering.PricePerHour.PerUnit);
    }

    // A tier priced at zero or below is not "free" — it is a catalog row that bills nothing while
    // consuming real capacity.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-311.256)]
    public void VmOffering_RejectsNonPositivePrice(decimal price)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            VmOffering.Create("m", "Medium", 1.0m, 2, 4096, price));

        Assert.Equal("pricePerHour", ex.ParamName);
    }

    // The approved table carries up to 6 decimal places (161.388, 1268.100). A rounding loss
    // anywhere in the chain is a billing discrepancy, not a display glitch.
    [Fact]
    public void VmOffering_PreservesSixDecimalPlaces()
    {
        var offering = VmOffering.Create("s", "Small", 0.5m, 1, 2048, 161.388001m);

        Assert.Equal(161.388001m, offering.PricePerHour.PerUnit);
    }

    // ── VolumeOffering ──────────────────────────────────────────────

    [Fact]
    public void VolumeOffering_CarriesItsOwnPrice()
    {
        var offering = VolumeOffering.Create("standard", "Standard", 0, true, pricePerGbPerHour: 1.57914m);

        Assert.Equal(1.57914m, offering.PricePerGbPerHour.PerUnit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.57914)]
    public void VolumeOffering_RejectsNonPositivePrice(decimal price)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            VolumeOffering.Create("standard", "Standard", 0, true, price));

        Assert.Equal("pricePerGbPerHour", ex.ParamName);
    }

    // ── SnapshotOffering ────────────────────────────────────────────

    [Fact]
    public void SnapshotOffering_CarriesItsOwnPrice()
    {
        var offering = SnapshotOffering.Create("standard", "Standard", pricePerHour: 1.8m);

        Assert.Equal(1.8m, offering.PricePerHour.PerUnit);
    }

    // Snapshot is the one offering where zero is a real operational choice, not a mistake: it means
    // "snapshot billing is off". Distinct from a MISSING catalog row, which the meter treats as a system
    // fault (see SnapshotMeterServiceTests).
    [Fact]
    public void SnapshotOffering_AllowsZeroPrice_BecauseZeroMeansBillingOff()
    {
        var offering = SnapshotOffering.Create("standard", "Standard", 0m);

        Assert.Equal(0m, offering.PricePerHour.PerUnit);
    }

    [Fact]
    public void SnapshotOffering_RejectsNegativePrice()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SnapshotOffering.Create("standard", "Standard", -1m));

        Assert.Equal("pricePerHour", ex.ParamName);
    }

    // The meter has no snapshot-offering id to resolve from (snapshots do not carry one), so it
    // looks up this well-known id. Pinning it here means a rename
    // breaks a test rather than silently breaking snapshot billing.
    [Fact]
    public void SnapshotOffering_DefaultId_IsStandard()
    {
        Assert.Equal("standard", SnapshotOffering.DefaultId);
    }

    // ── Price change ────────────────────────────────────────────────

    [Fact]
    public void VmOffering_ChangePrice_ReplacesPrice()
    {
        var offering = VmOffering.Create("m", "Medium", 1.0m, 2, 4096, 311.256m);

        offering.ChangePrice(400_000m);

        Assert.Equal(400_000m, offering.PricePerHour.PerUnit);
    }

    // Same guard as Create, on the same aggregate — an admin typo must not be able to reach a state
    // the factory refuses. The endpoint validator answers 400 for this; the aggregate is the backstop
    // for anything that bypassed it.
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void VmOffering_ChangePrice_RejectsNonPositive(decimal price)
    {
        var offering = VmOffering.Create("m", "Medium", 1.0m, 2, 4096, 311.256m);

        var ex = Assert.Throws<ArgumentException>(() => offering.ChangePrice(price));

        Assert.Equal("pricePerHour", ex.ParamName);
        Assert.Equal(311.256m, offering.PricePerHour.PerUnit);   // old price survives a rejected change
    }
}
