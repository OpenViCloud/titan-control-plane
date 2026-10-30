using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Invoice;
using Titan.Domain.Aggregates.PaymentGatewayTransaction;
using Titan.Domain.Aggregates.TopUp;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

// Statuses/sources are C# enums stored via HasConversion<string>. The claim that carries risk is "a row
// written as a plain string still reads back as the right enum, and a row written now holds the exact same
// string". Only a real Postgres proves it: the InMemory provider has no store type, and no migration
// rewrote the rows, so the converter is the ONLY thing standing between an existing "Confirmed" row and a
// materialization failure.
//
// Each test RAW-INSERTS the string production rows contain and reads it back through EF — proof that the
// enum maps onto the stored value, not just onto values this same code just wrote. Category=Integration —
// its own CI step (no Docker locally).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class EnumStatusRoundTripDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task TopUp_OldStringRow_ReadsBackAsEnum_AndPersistsSameString()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        await using (var db = pg.NewDbContext())
        {
            // Exactly what an existing row holds: text 'Confirmed' / 'SePay'.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO core.top_ups
                    (id, tenant_id, amount, currency, status, payment_ref, source, created_at, confirmed_at, confirmed_by)
                VALUES
                    ({0}, {1}, 100, 'VND', 'Confirmed', 'TITAN-ROUNDTRIP', 'SePay', now(), now(), 'sys')
                """,
                id, tenantId);
        }

        await using (var db = pg.NewDbContext())
        {
            var topUp = await db.TopUps.SingleAsync(t => t.Id == id);
            Assert.Equal(TopUpStatus.Confirmed, topUp.Status);
            Assert.Equal(TopUpSource.SePay, topUp.Source);
        }

        // The enum persists back as the identical string, not an integer.
        await using (var db = pg.NewDbContext())
        {
            var stored = (await db.Database
                .SqlQueryRaw<string>("SELECT status AS \"Value\" FROM core.top_ups WHERE id = {0}", id)
                .ToListAsync())[0];
            Assert.Equal("Confirmed", stored);
        }
    }

    [Fact]
    public async Task PaymentGatewayTransaction_OldStringRow_ReadsBackAsEnum()
    {
        var txnId = $"sepay-roundtrip-{Guid.NewGuid()}";
        await using (var db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO core.payment_transactions
                    (id, amount, gateway, status, received_at, transaction_date)
                VALUES
                    ({0}, 100, 'Vietcombank', 'AmountMismatch', now(), now())
                """,
                txnId);
        }

        await using (var db = pg.NewDbContext())
        {
            var txn = await db.PaymentGatewayTransactions.SingleAsync(t => t.Id == txnId);
            Assert.Equal(PaymentGatewayTransactionStatus.AmountMismatch, txn.Status);
        }
    }

    // Invoice.Status is the enum InvoiceStatus stored via HasConversion<string>(), and is the concurrency
    // token. This raw-inserts the exact multi-word string a stored row holds ('SettledByWallet') and proves
    // it materializes as the enum, then re-persists as the identical string. Storing the enum's integer
    // instead would make this row fail to materialize.
    [Fact]
    public async Task Invoice_OldStringRow_ReadsBackAsEnum_AndPersistsSameString()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        await using (var db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO core.invoices
                    (id, tenant_id, period_year, period_month, status, issued_at, due_at, paid_at, paid_by, total, currency)
                VALUES
                    ({0}, {1}, 2026, 8, 'SettledByWallet', now(), now(), now(), 'wallet', 100, 'VND')
                """,
                id, tenantId);
        }

        await using (var db = pg.NewDbContext())
        {
            var invoice = await db.Invoices.SingleAsync(i => i.Id == id);
            Assert.Equal(InvoiceStatus.SettledByWallet, invoice.Status);
        }

        await using (var db = pg.NewDbContext())
        {
            var stored = (await db.Database
                .SqlQueryRaw<string>("SELECT status AS \"Value\" FROM core.invoices WHERE id = {0}", id)
                .ToListAsync())[0];
            Assert.Equal("SettledByWallet", stored);
        }
    }
}
