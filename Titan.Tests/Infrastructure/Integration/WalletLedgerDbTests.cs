using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletTransaction;
using Titan.Domain.Exceptions;
using Titan.Domain.Services;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops architecture/money-and-wallet.md#signed-ledger: the balance and its row reach Postgres in one commit or
// not at all. WalletLedgerTests pins the rows the ledger builds; this reads them back. Category=Integration.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletLedgerDbTests(PostgresFixture pg)
{
    private static WalletLedger LedgerOn(TitanDbContext db) => new(new WalletTransactionRepository(db));

    private async Task<Guid> SeedWalletAsync(long balance)
    {
        var tenantId = Guid.NewGuid();
        await using var db = pg.NewDbContext();
        var repo = new WalletRepository(db);
        var wallet = Wallet.Open(tenantId, "VND");
        repo.Add(wallet);
        LedgerOn(db).Credit(wallet, WalletTransactionType.TopUp, balance, Guid.NewGuid());
        await repo.SaveChangesAsync(default);
        return tenantId;
    }

    private async Task<(long Balance, long LedgerSum, int Rows)> ReadMoneyAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        Wallet wallet = await db.Wallets.AsNoTracking().SingleAsync(w => w.TenantId == tenantId);
        List<WalletTransaction> rows = await db.WalletTransactions.AsNoTracking()
            .Where(t => t.WalletId == wallet.Id).ToListAsync();
        return (wallet.AvailableAmount, rows.Sum(t => t.Amount), rows.Count);
    }

    [Fact]
    public async Task CreditThenDebit_ReachPostgres_AndTheLedgerSumsToTheBalance()
    {
        Guid tenantId = await SeedWalletAsync(1_000L);

        await using (var db = pg.NewDbContext())
        {
            var repo = new WalletRepository(db);
            Wallet? wallet = await repo.GetByTenantForUpdateAsync(tenantId, default);
            Assert.NotNull(wallet);
            LedgerOn(db).Debit(wallet, 300L, Guid.NewGuid());
            await repo.SaveChangesAsync(default);
        }

        (long balance, long ledgerSum, int rows) = await ReadMoneyAsync(tenantId);
        Assert.Equal(700L, balance);
        Assert.Equal(700L, ledgerSum);
        Assert.Equal(2, rows);
    }

    [Fact]
    public async Task TheLoserOfTheVersionRace_CommitsNeitherBalanceNorRow()
    {
        Guid tenantId = await SeedWalletAsync(1_000L);

        await using var first = pg.NewDbContext();
        await using var second = pg.NewDbContext();
        var firstRepo = new WalletRepository(first);
        var secondRepo = new WalletRepository(second);
        Wallet? firstCopy = await firstRepo.GetByTenantForUpdateAsync(tenantId, default);
        Wallet? secondCopy = await secondRepo.GetByTenantForUpdateAsync(tenantId, default);
        Assert.NotNull(firstCopy);
        Assert.NotNull(secondCopy);

        LedgerOn(first).Debit(firstCopy, 300L, Guid.NewGuid());
        LedgerOn(second).Debit(secondCopy, 300L, Guid.NewGuid());
        await firstRepo.SaveChangesAsync(default);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => secondRepo.SaveChangesAsync(default));

        (long balance, long ledgerSum, int rows) = await ReadMoneyAsync(tenantId);
        Assert.Equal(700L, balance);
        Assert.Equal(700L, ledgerSum);
        Assert.Equal(2, rows);
    }

    [Fact]
    public async Task ASecondRowForTheSameDocument_RollsTheBalanceBackToo()
    {
        Guid tenantId = await SeedWalletAsync(1_000L);
        var invoiceId = Guid.NewGuid();

        await using (var db = pg.NewDbContext())
        {
            var repo = new WalletRepository(db);
            Wallet? wallet = await repo.GetByTenantForUpdateAsync(tenantId, default);
            Assert.NotNull(wallet);
            LedgerOn(db).Debit(wallet, 300L, invoiceId);
            await repo.SaveChangesAsync(default);
        }

        await using (var db = pg.NewDbContext())
        {
            var repo = new WalletRepository(db);
            Wallet? wallet = await repo.GetByTenantForUpdateAsync(tenantId, default);
            Assert.NotNull(wallet);
            LedgerOn(db).Debit(wallet, 300L, invoiceId);
            await Assert.ThrowsAsync<ConcurrencyConflictException>(() => repo.SaveChangesAsync(default));
        }

        (long balance, long ledgerSum, int rows) = await ReadMoneyAsync(tenantId);
        Assert.Equal(700L, balance);
        Assert.Equal(700L, ledgerSum);
        Assert.Equal(2, rows);
    }
}
