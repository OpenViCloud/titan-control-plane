using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Values;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// UsageResourceType is a C# enum stored via an EXPLICIT converter to its uppercase token
// (COMPUTE/DISK/SNAPSHOT). The claim that carries the risk: existing rows still read back, new rows
// still hold the identical token, and the idempotency ON CONFLICT still fires. Only a real Postgres
// proves it — and UsageRecordRepository.TryInsertAsync writes via RAW SQL,
// which bypasses the EF converter entirely, so the enum→token serialization there is a SEPARATE code
// path the InMemory/unit tests never exercise. Category=Integration — its own CI step (no Docker locally).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class UsageResourceTypeRoundTripDbTests(PostgresFixture pg)
{
    // A row holding the literal text 'COMPUTE' reads back as the enum, not a materialization failure.
    // Raw-inserted, so the token does not come from this code.
    [Fact]
    public async Task UsageRecord_OldUppercaseRow_ReadsBackAsEnum()
    {
        var id = Guid.NewGuid();
        await using (var db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO core.usage_records
                    (id, resource_type, resource_id, resource_name, tenant_id, bucket, interval_minutes,
                     quantity, usage_type, unit_price_per_hour, cost, currency, collected_at)
                VALUES
                    ({0}, 'COMPUTE', {1}, 'web-01', {2}, now(), 10, 0, 'RUNNING_VM', 1.5, 1, 'VND', now())
                """,
                id, Guid.NewGuid(), Guid.NewGuid());
        }

        await using (var db = pg.NewDbContext())
        {
            var record = await db.UsageRecords.SingleAsync(r => r.Id == id);
            Assert.Equal(UsageResourceType.Compute, record.ResourceType);
        }
    }

    // A record inserted through the RAW-SQL TryInsertAsync path lands as the exact uppercase
    // token, NOT the PascalCase member name "Compute". This is the path EF's converter does not touch.
    [Fact]
    public async Task TryInsert_PersistsUppercaseToken_ThroughRawSql()
    {
        var vmId = Guid.NewGuid();
        var record = UsageRecord.CreateCompute(
            vmId, "web-01", Guid.NewGuid(), DateTimeOffset.UtcNow, 10, Rate.Of(1.5m), "VND");

        await using (var db = pg.NewDbContext())
        {
            var inserted = await new UsageRecordRepository(db).TryInsertAsync(record, default);
            Assert.True(inserted);
        }

        await using (var db = pg.NewDbContext())
        {
            var stored = (await db.Database
                .SqlQueryRaw<string>(
                    "SELECT resource_type AS \"Value\" FROM core.usage_records WHERE id = {0}", record.Id)
                .ToListAsync())[0];
            Assert.Equal("COMPUTE", stored);
        }
    }

    // The enum→token mapping must not disturb idempotency: a second tick in the same
    // (resource_type, resource_id, bucket) is a no-op, still exactly one row. This would break if the
    // raw-SQL insert wrote a different token than the ON CONFLICT target column already holds.
    [Fact]
    public async Task SecondTickSameBucket_IsNoOp()
    {
        var vmId = Guid.NewGuid();
        // Anchored inside a 10-minute bucket, NOT DateTimeOffset.UtcNow: at :03 and :04 both floor to
        // :00, but UtcNow.AddMinutes(1) crosses the bucket boundary ~10% of the time (whenever the
        // minute is ≡ 9 mod 10, e.g. 08:29→08:30), making the second tick a real new row and the test
        // flaky on the wall clock. The bucket, not the wall clock, is what this test is about.
        var at = new DateTimeOffset(2026, 1, 1, 9, 3, 0, TimeSpan.Zero);
        var first = UsageRecord.CreateCompute(vmId, "web-01", Guid.NewGuid(), at, 10, Rate.Of(1.5m), "VND");
        // Same resource + same bucket (floored), different row id — the duplicate a double tick produces.
        var second = UsageRecord.CreateCompute(vmId, "web-01", first.TenantId, at.AddMinutes(1), 10, Rate.Of(1.5m), "VND");

        await using var db = pg.NewDbContext();
        var repo = new UsageRecordRepository(db);

        Assert.True(await repo.TryInsertAsync(first, default));
        Assert.False(await repo.TryInsertAsync(second, default));

        var count = (await db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM core.usage_records WHERE resource_id = {0}", vmId)
            .ToListAsync())[0];
        Assert.Equal(1, count);
    }

    // invoice_items.resource_type round-trips through the EF converter: a
    // persisted InvoiceItem stores the uppercase token and reads back as the enum.
    [Fact]
    public async Task InvoiceItem_RoundTripsThroughEfConverter()
    {
        var tenantId = Guid.NewGuid();
        var item = InvoiceItem.Create(UsageResourceType.Disk, Guid.NewGuid(), "vol-01", 42L, "VND");
        var invoice = Invoice.Generate(tenantId, 2026, 9, DateTimeOffset.UtcNow, [item], "VND");

        await using (var db = pg.NewDbContext())
        {
            db.Invoices.Add(invoice);
            await db.SaveChangesAsync();
        }

        await using (var db = pg.NewDbContext())
        {
            var stored = (await db.Database
                .SqlQueryRaw<string>(
                    "SELECT resource_type AS \"Value\" FROM core.invoice_items WHERE id = {0}", item.Id)
                .ToListAsync())[0];
            Assert.Equal("DISK", stored);

            var read = await db.Invoices.SelectMany(i => i.Items).SingleAsync(x => x.Id == item.Id);
            Assert.Equal(UsageResourceType.Disk, read.ResourceType);
        }
    }
}
