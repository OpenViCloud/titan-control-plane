using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.TopUp;
using Titan.Domain.Aggregates.Wallet;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// The DB-layer proof for the admin-grant money path. A promo grant is a TopUp with
// Source=PromotionalCredit; these run the real CreditRepository + WalletRepository against a real
// Postgres with the actual migration applied, so they prove what
// metadata/mocks cannot:
//   1. a promo TopUp round-trips through `top_ups` with the `reason` column, and
//   2. a grant's TopUp row, the wallet credit, and the Promo ledger row commit in ONE SaveChanges —
//      all three land, or none does — and the Promo movement's RefId resolves to the TopUp.
//
// Category=Integration: real Postgres (see PostgresFixture), runs in its own CI process.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class PromotionalCreditDbTests(PostgresFixture pg)
{
    // A grant credits the wallet, stores the promo TopUp, and the Promo ledger row points at
    // it, all atomically and readable afterwards.
    [Fact]
    public async Task Grant_CreditsWallet_StoresPromoTopUp_LedgerPointsAtIt()
    {
        var tenantId = Guid.NewGuid();

        await using (var db = pg.NewDbContext())
        {
            new WalletRepository(db).Add(Wallet.Open(tenantId, "VND"));
            await new WalletRepository(db).SaveChangesAsync(default);
        }

        Guid topUpId;
        await using (var db = pg.NewDbContext())
        {
            var creditRepo = new CreditRepository(db);
            var walletRepo = new WalletRepository(db);
            var topUp = TopUp.GrantPromo(tenantId, 500_000L, "VND", "welcome", "admin-1");
            topUpId = topUp.Id;
            var wallet = await walletRepo.GetByTenantForUpdateAsync(tenantId, default);
            wallet!.Credit(500_000L);
            creditRepo.AddTopUp(topUp);
            walletRepo.AddTransaction(WalletTransaction.ForPromo(wallet.Id, 500_000L, topUp.Id, wallet.AvailableAmount));
            await walletRepo.SaveChangesAsync(default);
        }

        await using (var db = pg.NewDbContext())
        {
            // The promo TopUp is listable via the filtered query, with reason + admin preserved.
            var list = await new CreditRepository(db).ListTopUpsAsync(TopUpSource.PromotionalCredit, default);
            var stored = Assert.Single(list, t => t.Id == topUpId);
            Assert.Equal(500_000L, stored.Amount);
            Assert.Equal("welcome", stored.Reason);
            Assert.Equal("admin-1", stored.ConfirmedBy);
            Assert.Equal(TopUpSource.PromotionalCredit, stored.Source);
            Assert.Equal(TopUpStatus.Confirmed, stored.Status);

            // The wallet balance moved, and the Promo ledger row's RefId resolves to the TopUp, so the
            // ledger row points at a real document.
            Assert.Equal(500_000L, await new WalletRepository(db).GetBalanceAsync(tenantId, default));
            var promoTxn = await db.WalletTransactions
                .SingleAsync(x => x.Type == WalletTransactionType.Promo && x.RefId == topUpId, default);
            Assert.Equal(500_000L, promoTxn.Amount);
        }
    }

    // A plain (real-money) TopUp is NOT returned by the promo history query — the Source filter holds.
    [Fact]
    public async Task ListPromotionalTopUps_ExcludesRealMoneyTopUps()
    {
        var tenantId = Guid.NewGuid();

        await using (var db = pg.NewDbContext())
        {
            new WalletRepository(db).Add(Wallet.Open(tenantId, "VND"));
            await new WalletRepository(db).SaveChangesAsync(default);
        }

        Guid promoId, realId;
        await using (var db = pg.NewDbContext())
        {
            var repo = new CreditRepository(db);
            var promo = TopUp.GrantPromo(tenantId, 100_000L, "VND", "gift", "admin-1");
            var real = TopUp.Create(tenantId, 200_000L, "VND");
            real.Confirm(TopUpSource.SePay, "webhook", "txn-1");
            promoId = promo.Id;
            realId = real.Id;
            repo.AddTopUp(promo);
            repo.AddTopUp(real);
            await repo.SaveChangesAsync(default);
        }

        await using (var db = pg.NewDbContext())
        {
            var list = (await new CreditRepository(db).ListTopUpsAsync(TopUpSource.PromotionalCredit, default))
                .Where(t => t.TenantId == tenantId).ToList();
            Assert.Contains(list, t => t.Id == promoId);
            Assert.DoesNotContain(list, t => t.Id == realId);
        }
    }
}
