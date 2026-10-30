using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class UsageRecordRepository(TitanDbContext db) : IUsageRecordRepository
{
    // EF Core has no "INSERT ... ON CONFLICT DO NOTHING", so the meter's idempotency guard is raw SQL against
    // the (resource_type, resource_id, bucket) unique index: a duplicate returns 0 rows instead of throwing
    // (titan-ops architecture/money-and-wallet.md#append-only-metering). The index is pinned by UsageRecordSchemaTests.
    public async Task<bool> TryInsertAsync(UsageRecord record, CancellationToken ct)
    {
        // Raw SQL bypasses EF value converters, so ResourceType (.ToDbString()) and the Rate (.PerUnit) are
        // converted by hand: interpolating them directly throws on Postgres ("no store type mapping"), which
        // only UsageResourceTypeRoundTripDbTests can see.
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO core.usage_records
                (id, resource_type, resource_id, resource_name, tenant_id, bucket, interval_minutes,
                 quantity, usage_type, unit_price_per_hour, cost, currency, collected_at)
            VALUES
                ({record.Id}, {record.ResourceType.ToDbString()}, {record.ResourceId}, {record.ResourceName},
                 {record.TenantId}, {record.Bucket}, {record.IntervalMinutes}, {record.Quantity},
                 {record.UsageType}, {record.UnitPricePerHour.PerUnit}, {record.Cost},
                 {record.Currency}, {record.CollectedAt})
            ON CONFLICT (resource_type, resource_id, bucket) DO NOTHING
            """, ct);
        return rows == 1;
    }

    public async Task<IReadOnlyList<UsageRecord>> GetByResourceAndMonthAsync(
        UsageResourceType resourceType, Guid resourceId, int year, int month, CancellationToken ct) =>
        await db.UsageRecords
            .AsNoTracking()
            .Where(r => r.ResourceType == resourceType && r.ResourceId == resourceId
                        && r.Bucket.Year == year && r.Bucket.Month == month)
            .OrderBy(r => r.Bucket)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UsageRecord>> GetByTenantAndMonthAsync(
        Guid tenantId, int year, int month, CancellationToken ct) =>
        await db.UsageRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Bucket.Year == year && r.Bucket.Month == month)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetDistinctTenantIdsForMonthAsync(
        int year, int month, CancellationToken ct) =>
        await db.UsageRecords
            .AsNoTracking()
            .Where(r => r.Bucket.Year == year && r.Bucket.Month == month)
            .Select(r => r.TenantId)
            .Distinct()
            .ToListAsync(ct);
}
