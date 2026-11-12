using Titan.Domain.Exceptions;
using UsageRecordAggregate = Titan.Domain.Aggregates.UsageRecord.UsageRecord;

namespace Titan.Application.Services.Billing;

/// The month a caller asks for, checked by the handler before it reads the ledger. The rule is UsageRecord.IsAKeptMonth;
/// a month whose first buckets are already deleted would read short, or bill short, without a word
/// (titan-ops features/billing/metering/design.md#usage-retention).
public static class BillingInput
{
    public static void EnsureKeptMonth(int year, int month, DateTimeOffset now)
    {
        if (!UsageRecordAggregate.IsAKeptMonth(year, month, now))
        {
            throw new BusinessException("USAGE_PERIOD_EXPIRED",
                $"Usage of {year:D4}-{month:D2} is no longer kept: usage records are kept {UsageRecordAggregate.KeptDays} days. "
                + "The invoice of that month still holds its amounts.");
        }
    }
}
