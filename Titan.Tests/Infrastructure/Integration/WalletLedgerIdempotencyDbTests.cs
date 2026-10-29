using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Services.Billing.TopUp;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Billing.TopUp;
using Titan.Domain.Aggregates.Billing.Wallet;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// The double-credit race, under real concurrency on Postgres. Confirm does not touch the wallet: it
// enqueues ONE credit WalletMutation guarded by UNIQUE(ref_type, ref_id), and the single-writer consumer
// applies it later (titan-ops architecture/money-and-wallet.md#single-writer-outbox). Two concurrent confirms of
// the same top-up must leave EXACTLY ONE mutation (WalletRepository translates the loser's unique
// violation to ConcurrencyConflictException, which the confirm rule catches), and DRAINING it must credit the wallet exactly once.
// The ledger UNIQUE(type, ref_id) stays as defense-in-depth on the consumer path; only its declaration is
// pinned (WalletSchemaTests.WalletTransactionTypeRefId_IsUnique, titan-ops features/billing/wallet/design.md#wallet-debts).
//
// The stress loop proves the mutation index actually blocks the race (WalletMutationSchemaTests pins that it
// is declared; InMemory does not enforce it). Category=Integration — its own CI step; the dev box has no Docker.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletLedgerIdempotencyDbTests(PostgresFixture pg)
{
    private const int Iterations = 150;
    // The confirm rule both channels share (TopUpConfirmation), over real repositories.
    private static ConfirmRule NewService(TitanDbContext db, IWalletMutationPublisher bus) => new(db, bus);

    private sealed class ConfirmRule(TitanDbContext db, IWalletMutationPublisher bus)
    {
        public Task ConfirmAsync(Guid topUpId, TopUpSource source, string confirmedBy, string? paymentTxnRef)
            => TopUpConfirmation.ConfirmAsync(
                new CreditRepository(db), new WalletRepository(db), new WalletMutationRepository(db), bus,
                topUpId, source, confirmedBy, paymentTxnRef, confirmedAt: null, CancellationToken.None);
    }

    private async Task SeedWalletAsync(Guid tenantId)
    {
        await using var db = pg.NewDbContext();
        db.Wallets.Add(Wallet.Open(tenantId, "VND"));
        await db.SaveChangesAsync();
    }

    private async Task<TopUp> SeedPendingTopUpAsync(Guid tenantId, long amount)
    {
        var topUp = TopUp.Create(tenantId, amount, "VND");
        await using var db = pg.NewDbContext();
        db.TopUps.Add(topUp);
        await db.SaveChangesAsync();
        return topUp;
    }

    // Applies a mutation exactly as the single-writer consumer would.
    private async Task DrainAsync(Guid mutationId)
    {
        await using var db = pg.NewDbContext();
        await new WalletMutationConsumer(new WalletMutationRepository(db), new WalletRepository(db), new InvoiceRepository(db))
            .ApplyAsync(mutationId);
    }

    // Two confirms of the same Pending top-up fired at once, repeated many times to exercise every
    // interleaving. Whichever channel wins, EXACTLY ONE credit mutation exists, the stored top-up is
    // Confirmed, and draining that mutation credits the wallet exactly once. Fails on the FIRST bad iteration
    // with the observed values, so a regression (e.g. the mutation unique index removed) names itself.
    [Fact]
    public async Task ConcurrentConfirm_RepeatedManyTimes_EnqueuesOneMutation_CreditsExactlyOnce()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var tenantId = Guid.NewGuid();
            await SeedWalletAsync(tenantId);
            var topUp = await SeedPendingTopUpAsync(tenantId, 100L);
            var bus = WalletBus.None();

            try
            {
                await using var dbA = pg.NewDbContext();
                await using var dbB = pg.NewDbContext();
                var runA = NewService(dbA, bus).ConfirmAsync(topUp.Id, TopUpSource.SePay, "sys", $"txn-A-{i}");
                var runB = NewService(dbB, bus).ConfirmAsync(topUp.Id, TopUpSource.Manual, "admin", $"txn-B-{i}");
                await Task.WhenAll(runA, runB);
            }
            catch (Exception ex)
            {
                // A confirm must never surface a raw exception to a caller — a duplicate is recovered
                // inside the confirm rule. An escaping exception (e.g. a unique violation not translated, or
                // a deadlock) IS a regression; name the iteration and the type.
                Assert.Fail($"iter {i}: ConfirmAsync threw {ex.GetType().Name}: {ex.Message}");
                throw; // unreachable, keeps the compiler happy
            }

            // Exactly one credit mutation survived the race — the double-credit is killed at the source.
            List<WalletMutation> mutations;
            await using (var db = pg.NewDbContext())
                mutations = await db.WalletMutations.AsNoTracking()
                    .Where(c => c.RefType == "TopUp" && c.RefId == topUp.Id).ToListAsync();
            Assert.True(mutations.Count == 1,
                $"iter {i}: mutations={mutations.Count} (expected 1) — double or missing enqueue");

            // titan-ops architecture/http-contract.md#commands-return-no-data: neither confirm returns the TopUp, so
            // which caller won is not observable. What is observable is the one stored row: Confirmed, by
            // exactly one of the two channels.
            await using (var db = pg.NewDbContext())
            {
                var stored = await db.TopUps.AsNoTracking().SingleAsync(t => t.Id == topUp.Id);
                Assert.True(stored.Status == TopUpStatus.Confirmed,
                    $"iter {i}: status={stored.Status} (expected Confirmed)");
                Assert.True(stored.Source is TopUpSource.SePay or TopUpSource.Manual,
                    $"iter {i}: source={stored.Source} — neither racer's channel");
            }

            // Draining the one mutation credits the wallet exactly once, whatever the interleaving was.
            await DrainAsync(mutations[0].Id);
            await using var check = pg.NewDbContext();
            var movements = await check.WalletTransactions.AsNoTracking().CountAsync(t => t.RefId == topUp.Id);
            var balance = await new WalletRepository(check).GetBalanceAsync(tenantId, default);
            Assert.True(movements == 1,
                $"iter {i}: movements={movements} (expected 1 after drain) — double or missing credit");
            Assert.True(balance == 100L,
                $"iter {i}: balance={balance} (expected 100) — credited wrong amount");
        }
    }
}
