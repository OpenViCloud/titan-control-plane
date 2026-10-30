using Titan.Domain.Aggregates.UsageRecord;

namespace Titan.Domain.Repositories;

public interface IUsageRecordRepository
{
    /// INSERT ... ON CONFLICT (resource_type, resource_id, bucket) DO NOTHING: a duplicate tick is a no-op,
    /// never an exception (titan-ops architecture/money-and-wallet.md#append-only-metering). Returns whether a row
    /// was inserted.
    Task<bool> TryInsertAsync(UsageRecord record, CancellationToken ct);

    /// Ordered by bucket.
    Task<IReadOnlyList<UsageRecord>> GetByResourceAndMonthAsync(
        UsageResourceType resourceType, Guid resourceId, int year, int month, CancellationToken ct);

    Task<IReadOnlyList<UsageRecord>> GetByTenantAndMonthAsync(Guid tenantId, int year, int month, CancellationToken ct);

    /// Invoice generation walks this, so a tenant with no usage that month gets no invoice.
    Task<IReadOnlyList<Guid>> GetDistinctTenantIdsForMonthAsync(int year, int month, CancellationToken ct);
}
