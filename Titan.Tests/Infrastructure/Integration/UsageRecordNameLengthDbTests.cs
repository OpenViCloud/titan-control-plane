using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// A usage record keeps the name the resource had when it was metered, and a volume or snapshot name may be
/// Volume.NameMaxLength characters long: it is stored whole, or the insert fails and the meter's run stops at that row
/// (titan-ops features/billing/metering/design.md#meters). Category=Integration — needs real Postgres: InMemory has no
/// column length.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class UsageRecordNameLengthDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task AVolumeNamedToTheLimit_IsStored_WithItsWholeName()
    {
        string name = new('v', Volume.NameMaxLength);
        UsageRecord record = UsageRecord.CreateDisk(Guid.NewGuid(), name, Guid.NewGuid(), DateTimeOffset.UtcNow, 10,
            20, Rate.Of(1m), "VND");

        await using (TitanDbContext db = pg.NewDbContext())
        {
            Assert.True(await new UsageRecordRepository(db).TryInsertAsync(record, CancellationToken.None));
        }

        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(name, read.UsageRecords.Single(r => r.Id == record.Id).ResourceName);
    }
}
