using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.PaymentGatewayTransaction;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Repositories;

public class PaymentGatewayTransactionRepository(TitanDbContext db) : IPaymentGatewayTransactionRepository
{
    public Task<bool> ExistsAsync(string txnId, CancellationToken ct) =>
        db.PaymentGatewayTransactions.AsNoTracking().AnyAsync(t => t.Id == txnId, ct);

    public async Task<bool> TryAddAsync(PaymentGatewayTransaction txn, CancellationToken ct)
    {
        db.PaymentGatewayTransactions.Add(txn);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent webhook for the same SePay txn id won the PK race — already recorded.
            // Detach so a later SaveChanges on this scoped context does not retry it.
            db.Entry(txn).State = EntityState.Detached;
            return false;
        }
    }

    public Task<PaymentGatewayTransaction?> GetAsync(string txnId, CancellationToken ct) =>
        db.PaymentGatewayTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == txnId, ct);
}
