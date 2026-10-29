using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Billing.UsageRecord;
using Titan.Domain.Values;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// A usage record stores its raw cost (titan-ops architecture/money-and-wallet.md#round-once). The meter writes through raw SQL,
// which no EF mapping checks, so only Postgres shows whether a fractional cost survives the write: a bigint column, or
// an integer parameter, would store 0 for the 0.26319 ₫ of a 1 GB volume. Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class UsageCostDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task AFractionalCost_WrittenByTheMeter_ReadsBackUnrounded()
    {
        var record = UsageRecord.CreateDisk(
            Guid.NewGuid(), "small-vol", Guid.NewGuid(), new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero), 10, 1,
            Rate.Of(1.57914m), "VND");
        Assert.Equal(0.26319m, record.Cost);

        await using (var db = pg.NewDbContext())
        {
            Assert.True(await new UsageRecordRepository(db).TryInsertAsync(record, CancellationToken.None));
        }

        await using (var db = pg.NewDbContext())
        {
            var stored = await db.UsageRecords.SingleAsync(r => r.Id == record.Id);
            Assert.Equal(0.26319m, stored.Cost);
        }
    }
}
