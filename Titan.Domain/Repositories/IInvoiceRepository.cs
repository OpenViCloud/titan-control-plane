using Titan.Domain.Aggregates.Invoice;

namespace Titan.Domain.Repositories;

public interface IInvoiceRepository
{
    /// False when (TenantId, PeriodYear, PeriodMonth) already has one: the unique index is generation's
    /// idempotency guard.
    Task<bool> TryAddAsync(Invoice invoice, CancellationToken ct);

    /// Checked first, so re-running generation for a billed period skips reading the month's usage.
    Task<bool> ExistsForPeriodAsync(Guid tenantId, int year, int month, CancellationToken ct);

    /// Untracked.
    Task<IReadOnlyList<Invoice>> ListByTenantAsync(Guid tenantId, CancellationToken ct);

    /// With its items, untracked. Ownership (tenant match) is the caller's job.
    Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct);

    Task<Invoice?> GetForUpdateAsync(Guid id, CancellationToken ct);

    /// Status == New, oldest issue first, untracked. Overdue is derived at read time and never stored
    /// (titan-ops architecture/money-and-wallet.md#invoice-frozen-snapshot), so filtering on "Overdue" would silently
    /// return nothing.
    Task<IReadOnlyList<Invoice>> ListOutstandingByTenantAsync(Guid tenantId, CancellationToken ct);

    /// All tenants, oldest issue first, untracked — the admin receivables read
    /// (titan-ops architecture/money-and-wallet.md#postpaid-no-brake).
    Task<IReadOnlyList<Invoice>> ListOutstandingAllAsync(CancellationToken ct);

    Task<IReadOnlyList<Invoice>> ListAllAsync(CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
