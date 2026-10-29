using Titan.Domain.Values;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Catalog.SnapshotOffering;
using Titan.Domain.Aggregates.Catalog.VmOffering;
using Titan.Domain.Aggregates.Catalog.VolumeOffering;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/money-and-wallet.md#price-belongs-to-offering — price is a NOT NULL column on the offering.
// The InMemory provider enforces neither nullability nor column types, so this pins the model metadata the
// migration depends on, the same way UsageRecordSchemaTests pins the append-only unique index.
//
// The negative half comes first on purpose: "prices are right" passes 100% even if
// the migration never ran, as long as the old table is still alive and something can still read it.
// Proving the old source is DEAD is a separate assertion, and it is the one that can actually fail.
public class OfferingPricingSchemaTests
{
    // The approved price table (VND, titan-ops architecture/money-and-wallet.md#money-is-long-vnd). Written here as
    // constants ON PURPOSE: the oracle must live outside the thing being measured. Reading these from the
    // seed builder would compare the data against the very code that produced it and pass no matter what
    // either one said.
    private static readonly (string Id, decimal Price)[] ApprovedVmPrices =
    [
        ("s", 161.388m), ("m", 311.256m), ("l", 634.050m), ("xl", 1268.100m)
    ];

    private const decimal ApprovedDiskPrice = 1.57914m;
    private const decimal ApprovedSnapshotPrice = 1.8m;

    private static TitanDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options);

    private static IEntityType EntityType(Type clrType)
    {
        using var db = NewContext();
        return db.Model.FindEntityType(clrType)!;
    }

    // ── The old source is dead, not merely unused ──────────────────────────

    [Fact]
    public void PricingRule_IsNoLongerAnEntityInTheModel()
    {
        using var db = NewContext();

        var pricingEntities = db.Model.GetEntityTypes()
            .Where(e => e.ClrType.Name.Contains("PricingRule", StringComparison.Ordinal))
            .Select(e => e.ClrType.Name)
            .ToArray();

        Assert.Empty(pricingEntities);
    }

    // Belt and braces for the same fact from the other side: no table by that name, whatever type
    // might map to it.
    [Fact]
    public void NoEntityMapsToThePricingRulesTable()
    {
        using var db = NewContext();

        Assert.DoesNotContain(db.Model.GetEntityTypes(), e => e.GetTableName() == "pricing_rules");
    }

    // A pricing-rule id on UsageRecord would carry a RESTRICT FK that holds the old table up.
    [Fact]
    public void UsageRecord_NoLongerCarriesAPricingRuleId()
    {
        var properties = EntityType(typeof(Titan.Domain.Aggregates.Billing.UsageRecord.UsageRecord))
            .GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("PricingRuleId", properties);
    }

    // The type itself is gone from the domain — a dangling interface would let a future service
    // resolve "the price" from a source that has no data behind it.
    [Fact]
    public void PricingRuleTypesAreGoneFromTheDomainAssembly()
    {
        var domainTypes = typeof(VmOffering).Assembly.GetTypes()
            .Where(t => t.Name.Contains("PricingRule", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(domainTypes);
    }

    // ── A tier cannot exist without a price ────────────────────────────────

    [Theory]
    [InlineData(typeof(VmOffering), "PricePerHour")]
    [InlineData(typeof(VolumeOffering), "PricePerGbPerHour")]
    [InlineData(typeof(SnapshotOffering), "PricePerHour")]
    public void PriceColumnIsNotNullable(Type offeringType, string propertyName)
    {
        var property = EntityType(offeringType).FindProperty(propertyName)!;

        Assert.False(property.IsNullable);
    }

    // One concept, one precision: a listed price and the price that was used to bill are the same
    // concept, so they must be stored the same way. The assertion below therefore compares the two
    // SIDES rather than restating a literal.
    //
    // The literal is pinned as well, and that is the point: comparing only the two sides passes if
    // BOTH drift to the same wrong precision, which is the direction a future "let's simplify the
    // money columns" change would take. Two assertions, two directions.
    [Theory]
    [InlineData(typeof(VmOffering), "PricePerHour")]
    [InlineData(typeof(VolumeOffering), "PricePerGbPerHour")]
    [InlineData(typeof(SnapshotOffering), "PricePerHour")]
    public void PriceColumn_HasTheSamePrecisionAsThePriceItWasBilledAt(Type offeringType, string propertyName)
    {
        var billed = ColumnTypeOf(typeof(Titan.Domain.Aggregates.Billing.UsageRecord.UsageRecord),
                                  "UnitPricePerHour");
        var listed = ColumnTypeOf(offeringType, propertyName);

        Assert.Equal("decimal(18,10)", billed);   // both non-null before they are compared
        Assert.Equal(billed, listed);
    }

    private static string ColumnTypeOf(Type clrType, string propertyName)
    {
        var property = EntityType(clrType).FindProperty(propertyName)!;
        return (string)property.FindAnnotation(RelationalAnnotationNames.ColumnType)!.Value!;
    }

    // ── Seeded prices match the approved table ─────────────────────────────

    [Fact]
    public void SeededVmTiers_MatchTheApprovedPriceTable()
    {
        var seeded = SeededPrices<VmOffering>("PricePerHour");

        // Assert both sides are populated BEFORE comparing them. Two empty collections compare
        // equal and report success, which is how a migration that did nothing passes a diff test.
        Assert.Equal(4, seeded.Count);
        Assert.Equal(4, ApprovedVmPrices.Length);

        foreach (var (id, expected) in ApprovedVmPrices)
            Assert.Equal(expected, seeded[id]);
    }

    [Fact]
    public void SeededVolumeTier_MatchesTheApprovedDiskPrice()
    {
        var seeded = SeededPrices<VolumeOffering>("PricePerGbPerHour");

        Assert.Equal(ApprovedDiskPrice, Assert.Single(seeded).Value);
    }

    [Fact]
    public void SeededSnapshotOffering_MatchesTheApprovedSnapshotPrice()
    {
        var seeded = SeededPrices<SnapshotOffering>("PricePerHour");

        var only = Assert.Single(seeded);
        Assert.Equal(SnapshotOffering.DefaultId, only.Key);
        Assert.Equal(ApprovedSnapshotPrice, only.Value);
    }

    // ── Prove the measurement can fail ─────────────────────────────────────

    // A comparison that cannot come out negative proves nothing about the thing it compares. This
    // feeds the same reader a deliberately wrong expectation and asserts it is rejected — so a green
    // SeededVmTiers_MatchTheApprovedPriceTable above means the seed matched, not that the reader
    // returned nothing.
    [Fact]
    public void TheSeedComparisonRejectsAWrongPrice()
    {
        var seeded = SeededPrices<VmOffering>("PricePerHour");

        Assert.NotEqual(999_999m, seeded["m"]);
        Assert.Throws<Xunit.Sdk.EqualException>(() => Assert.Equal(999_999m, seeded["m"]));
    }

    // Reads HasData seed rows out of the model. Two wrinkles worth naming: seed values are stored as
    // property-name -> value dictionaries rather than entity instances (so they are read by name),
    // and they live only on the DESIGN-TIME model — db.Model is the read-optimized runtime one and
    // throws for GetSeedData(). This is the same model the migration scaffolder reads, which is what
    // makes it the right thing to compare the approved table against.
    private static Dictionary<string, decimal> SeededPrices<T>(string priceProperty)
    {
        using var db = NewContext();
        return db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(T))!
            .GetSeedData()
            .ToDictionary(
                row => (string)row["Id"]!,
                // Seed values are Rate (titan-ops architecture/money-and-wallet.md#rate-value-object); unwrapped HERE
                // so the approved-price assertions keep reading as prices.
                row => ((Rate)row[priceProperty]!).PerUnit);
    }
}
