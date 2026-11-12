using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Values;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The retention pass on Postgres (titan-ops features/billing/metering/design.md#usage-retention): one DELETE, which
/// InMemory cannot run. The clock sits in 2001 so the pass reaches no row another test of the collection seeded.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class UsageRecordRetentionDbTests(PostgresFixture pg)
{
    private static readonly DateTimeOffset Now = new(2001, 4, 1, 0, 0, 0, TimeSpan.Zero);

    private static UsageRecord Record(Guid tenantId, DateTimeOffset bucket) =>
        UsageRecord.CreateCompute(Guid.NewGuid(), "vm-ret", tenantId, bucket, 10, Rate.Of(6000m), "VND");

    [Fact]
    public async Task OnePass_DeletesTheRecordOneBucketBeforeTheMark_KeepsTheOneOnIt_AndTheIssuedInvoiceStaysWhole()
    {
        Guid tenantId = Guid.NewGuid();
        DateTimeOffset mark = UsageRecord.KeptFrom(Now);
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), mark);
        UsageRecord old = Record(tenantId, mark.AddMinutes(-10));
        UsageRecord onTheMark = Record(tenantId, mark);
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            var usage = new UsageRecordRepository(seed);
            Assert.True(await usage.TryInsertAsync(old, default));
            Assert.True(await usage.TryInsertAsync(onTheMark, default));
        }

        // December 2000 was billed while every bucket of it was kept: the invoice copies the amount, as generation does.
        await using (TitanDbContext billing = pg.NewDbContext())
        {
            Invoice invoice = Invoice.Generate(tenantId, 2000, 12, mark,
                [InvoiceItem.Create(old.ResourceType, old.ResourceId, old.ResourceName, UsageRecord.TotalVnd([old]), "VND")],
                "VND");
            Assert.True(await new InvoiceRepository(billing).TryAddAsync(invoice, default));
        }
        long billed = await InvoiceTotalAsync(tenantId);
        Assert.Equal(UsageRecord.TotalVnd([old]), billed);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            int removed = await UsageRecordRetention.RunOnceAsync(new UsageRecordRepository(db), Now,
                NullLogger.Instance, default);
            Assert.True(removed >= 1, $"removed {removed}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        List<Guid> left = await read.UsageRecords.Where(r => r.TenantId == tenantId).Select(r => r.Id).ToListAsync();
        Assert.Equal([onTheMark.Id], left);
        Assert.Equal(billed, await InvoiceTotalAsync(tenantId));
    }

    private async Task<long> InvoiceTotalAsync(Guid tenantId)
    {
        await using TitanDbContext read = pg.NewDbContext();
        Invoice invoice = await read.Invoices.Include(i => i.Items).SingleAsync(i => i.TenantId == tenantId);
        Assert.Equal(invoice.Total, invoice.Items.Sum(i => i.Amount));
        return invoice.Total;
    }
}
