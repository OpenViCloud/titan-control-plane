using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox — the outbox idempotency guard, on real
// Postgres. UNIQUE(ref_type, ref_id) means one source document has EXACTLY ONE mutation row: two
// enqueues of the same (ref_type, ref_id) racing (two confirms of the same top-up) leave one survivor,
// the loser hitting 23505. Paying an invoice again reopens its row: InvoiceDebitRetryDbTests.
//
// This is the behaviour the schema test (WalletMutationSchemaTests) CANNOT prove: EF's InMemory
// provider does not enforce a unique index, so metadata alone would pass with the guard silently absent.
// Remove `.IsUnique()` from the config and this loop turns red (survivors=2). Category=Integration —
// its own CI step; the dev box has no Docker.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletMutationIdempotencyDbTests(PostgresFixture pg)
{
    private const int Iterations = 150;

    private async Task<int> CountAsync(string refType, Guid refId)
    {
        await using var db = pg.NewDbContext();
        return await db.WalletMutations.AsNoTracking()
            .CountAsync(c => c.RefType == refType && c.RefId == refId);
    }

    // Sequential: the second mutation with the same (ref_type, ref_id) is refused by the database, and
    // exactly ONE row survives. Asserting the COUNT (not merely "no throw") is what distinguishes the
    // guard working from both inserts silently landing — an empty-with-empty read would look identical.
    [Fact]
    public async Task SameRef_SecondInsert_IsRefused_AndLeavesExactlyOne()
    {
        var refId = Guid.NewGuid();
        var walletId = Guid.NewGuid();

        await using (var db1 = pg.NewDbContext())
        {
            db1.WalletMutations.Add(WalletMutation.ForCredit(walletId, 500_000L, "TopUp", refId));
            await db1.SaveChangesAsync();
        }

        await using (var db2 = pg.NewDbContext())
        {
            // A second mutation from the same source — even a different direction/amount — must not land.
            db2.WalletMutations.Add(WalletMutation.ForDebit(walletId, 1L, "TopUp", refId));
            await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
        }

        Assert.Equal(1, await CountAsync("TopUp", refId));
    }

    // The idempotency key is the PAIR: a TopUp and an Invoice can share a guid value without colliding.
    [Fact]
    public async Task DifferentRefType_SameRefId_BothLand()
    {
        var refId = Guid.NewGuid();
        var walletId = Guid.NewGuid();

        await using (var db = pg.NewDbContext())
        {
            db.WalletMutations.Add(WalletMutation.ForCredit(walletId, 10_000L, "TopUp", refId));
            db.WalletMutations.Add(WalletMutation.ForDebit(walletId, 10_000L, "Invoice", refId));
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await CountAsync("TopUp", refId));
        Assert.Equal(1, await CountAsync("Invoice", refId));
    }

    // Two enqueues of the same (ref_type, ref_id) fired at once, many times over, to exercise every
    // interleaving. Whichever wins, exactly one mutation survives; the loser's 23505 surfaces as a
    // DbUpdateException. Fails on the FIRST bad iteration with the observed count, so a regression names
    // itself in the CI log.
    [Fact]
    public async Task ConcurrentEnqueue_SameRef_RepeatedManyTimes_LeavesExactlyOne()
    {
        for (var i = 0; i < Iterations; i++)
        {
            var refId = Guid.NewGuid();
            var walletId = Guid.NewGuid();

            await using var dbA = pg.NewDbContext();
            await using var dbB = pg.NewDbContext();
            dbA.WalletMutations.Add(WalletMutation.ForCredit(walletId, 100L, "TopUp", refId));
            dbB.WalletMutations.Add(WalletMutation.ForCredit(walletId, 100L, "TopUp", refId));

            var outcomes = await Task.WhenAll(SaveIgnoringDuplicate(dbA), SaveIgnoringDuplicate(dbB));

            var survivors = await CountAsync("TopUp", refId);
            Assert.True(survivors == 1,
                $"iter {i}: survivors={survivors} (expected 1) — the unique guard let a duplicate mutation land");
            Assert.True(outcomes[0] || outcomes[1],
                $"iter {i}: neither enqueue committed — expected exactly one winner");
        }
    }

    // Returns true if this context committed the mutation, false if it lost the race (unique violation).
    // Any OTHER failure escapes — a deadlock or an untranslated error is a real regression, not the
    // expected duplicate.
    private static async Task<bool> SaveIgnoringDuplicate(TitanDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }
}
