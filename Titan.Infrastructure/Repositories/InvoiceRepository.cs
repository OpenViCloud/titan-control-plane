using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class InvoiceRepository(TitanDbContext db) : IInvoiceRepository
{
    public async Task<bool> TryAddAsync(Invoice invoice, CancellationToken ct)
    {
        db.Invoices.Add(invoice);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent generation run for the same period won the unique index. Detach the items too: left
            // Added, the next tenant's SaveChangesAsync on this scoped context retries them into an FK violation.
            // ToList first: EF fixup removes a detached child from Invoice.Items while this loop enumerates it.
            foreach (var item in invoice.Items.ToList())
                db.Entry(item).State = EntityState.Detached;
            db.Entry(invoice).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<bool> ExistsForPeriodAsync(Guid tenantId, int year, int month, CancellationToken ct) =>
        await db.Invoices
            .AsNoTracking()
            .AnyAsync(i => i.TenantId == tenantId && i.PeriodYear == year && i.PeriodMonth == month, ct);

    public async Task<IReadOnlyList<Invoice>> ListByTenantAsync(Guid tenantId, CancellationToken ct) =>
        await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .Where(i => i.TenantId == tenantId)
            .ToListAsync(ct);

    public async Task<Invoice?> GetByIdAsync(Guid id, CancellationToken ct) =>
        await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    /// Tracked on purpose: the caller mutates the result and SaveChangesAsync persists it.
    public Task<Invoice?> GetForUpdateAsync(Guid id, CancellationToken ct) =>
        db.Invoices.Include(i => i.Items).FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<IReadOnlyList<Invoice>> ListAllAsync(CancellationToken ct) =>
        await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Invoice>> ListOutstandingByTenantAsync(Guid tenantId, CancellationToken ct) =>
        await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .Where(i => i.TenantId == tenantId && i.Status == InvoiceStatus.New)
            .OrderBy(i => i.IssuedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Invoice>> ListOutstandingAllAsync(CancellationToken ct) =>
        await db.Invoices
            .AsNoTracking()
            .Include(i => i.Items)
            .Where(i => i.Status == InvoiceStatus.New)
            .OrderBy(i => i.IssuedAt)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
