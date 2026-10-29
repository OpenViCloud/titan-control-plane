namespace Titan.Application.Queries.Billing.UsageRecord.Contracts;

// The read side's own shape (titan-ops architecture/code-structure.md#no-contract-reuse).
public record VmCostQueryResult(
    Guid VmId, string Month, decimal TotalCost, string Currency,
    IReadOnlyList<UsageBreakdownLine> Breakdown);
