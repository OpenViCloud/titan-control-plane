using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/money-and-wallet.md#append-only-metering — at most one usage_record per
// (resource_type, resource_id, bucket), ever, enforced by the database rather than by application code
// checking-then-inserting (which two concurrent meter replicas, or two ticks in one process, can both
// win). EF's InMemory provider does not enforce unique indexes and does not support the raw SQL
// UsageRecordRepository.TryInsertAsync issues, so this pins the model metadata the migration and that
// SQL both depend on. The "second tick is a no-op" behavior itself is covered at the meter level
// (ComputeMeterServiceTests/DiskMeterServiceTests) against a fake repository.
public class UsageRecordSchemaTests
{
    private static IEntityType EntityType()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(UsageRecord))!;
    }

    private static IEnumerable<IIndex> Indexes() => EntityType().GetIndexes();

    // property.GetColumnType() throws against the InMemory provider (it tries to resolve a
    // relational type mapping that does not exist for it), even though the "Relational:ColumnType"
    // annotation HasColumnType(...) sets is present regardless of active provider. Reading the
    // annotation directly is what lets this pin the configured precision without a real database.
    private static string? ConfiguredColumnType(IProperty property) =>
        property.FindAnnotation(RelationalAnnotationNames.ColumnType)?.Value as string;

    [Fact]
    public void UniquePerResourceAndBucket()
    {
        var index = Indexes().SingleOrDefault(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).ToHashSet().SetEquals(
            [
                nameof(UsageRecord.ResourceType),
                nameof(UsageRecord.ResourceId),
                nameof(UsageRecord.Bucket)
            ]));

        Assert.NotNull(index);
    }

    // A VM (or volume) accumulates many rows over its life — one per bucket it existed in — so the
    // constraint must key on the bucket too, not just the resource.
    [Fact]
    public void UniquenessIsNotJustOnResource()
    {
        var index = Indexes().Single(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).ToHashSet().SetEquals(
            [
                nameof(UsageRecord.ResourceType),
                nameof(UsageRecord.ResourceId),
                nameof(UsageRecord.Bucket)
            ]));

        Assert.Equal(3, index.Properties.Count);
    }

    // A narrow column such as decimal(10,6) silently truncates a rate with more fractional digits on
    // storage, and no in-memory test sees it: the Cost math is full decimal precision regardless of the
    // column type; only a real Postgres write would round it. Pinning the configured column type makes a
    // narrowing fail this test instead of silently rounding money on write.
    [Fact]
    public void UnitPricePerHour_HasWidePrecisionToAvoidTruncatingFlatDiskRate()
    {
        var property = EntityType().FindProperty(nameof(UsageRecord.UnitPricePerHour))!;

        Assert.Equal("decimal(18,10)", ConfiguredColumnType(property));
    }

    // titan-ops architecture/money-and-wallet.md#round-once — Cost is the raw product of one record; whole VND happens once, on the
    // invoice line. A bigint column would round every record on write and bring back the 0 ₫ snapshot.
    [Fact]
    public void Cost_IsWideDecimal_TheRawProduct()
    {
        var property = EntityType().FindProperty(nameof(UsageRecord.Cost))!;

        Assert.Equal("decimal(18,10)", ConfiguredColumnType(property));
    }

    // ResourceType is a C# enum but MUST persist as its uppercase string token, as existing rows hold it.
    // Storing the enum's integer would silently re-map every existing "COMPUTE"/"DISK"/"SNAPSHOT" row and
    // break the idempotency ON CONFLICT this table depends on. Dropping the explicit conversion
    // (UsageResourceTypeExtensions.ToDbString/FromDbString) makes the converter null, and this red.
    [Fact]
    public void ResourceType_IsStoredAsString()
    {
        var property = EntityType().FindProperty(nameof(UsageRecord.ResourceType))!;
        var converter = property.GetValueConverter();

        Assert.NotNull(converter);
        Assert.Equal(typeof(string), converter!.ProviderClrType);
    }
}
