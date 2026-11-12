using Microsoft.EntityFrameworkCore;
using Titan.Application.Services.Billing.WalletMutations;
using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Aggregates.WalletMutation;
using Titan.Domain.Services;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops architecture/money-and-wallet.md#single-writer-outbox with several control plane replicas: NATS core hands
// every notify to every replica's subscriber, and each replica runs a sweeper, so one mutation reaches several
// consumers at once. Each consumer uses its own DbContext, as each replica does.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletTwoConsumersDbTests(PostgresFixture pg)
{
    private const int Replicas = 8;

    private static WalletMutationConsumer NewConsumer(TitanDbContext db) =>
        new(new WalletMutationRepository(db), new WalletRepository(db),
            new WalletMutationApply(new WalletRepository(db), new InvoiceRepository(db), new WalletLedger(new WalletTransactionRepository(db))));

    private async Task<Wallet> SeedWalletAsync()
    {
        var wallet = Wallet.Open(Guid.NewGuid(), "VND");
        await using var db = pg.NewDbContext();
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();
        return wallet;
    }

    private async Task<WalletMutation> EnqueueCreditAsync(Guid walletId, long amount)
    {
        var mutation = WalletMutation.Create(WalletMutationType.Credit, walletId, amount, "TopUp", Guid.NewGuid());
        await using var db = pg.NewDbContext();
        db.WalletMutations.Add(mutation);
        await db.SaveChangesAsync();
        return mutation;
    }

    // Every consumer is released at once; each returns the exception it threw, or null.
    private async Task<Exception?[]> ApplyAtOnceAsync(IReadOnlyList<Guid> mutationIds)
    {
        using var start = new SemaphoreSlim(0, mutationIds.Count);
        Task<Exception?>[] applies = mutationIds.Select(async mutationId =>
        {
            await using TitanDbContext db = pg.NewDbContext();
            await start.WaitAsync();
            try
            {
                await NewConsumer(db).ApplyAsync(mutationId);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }).ToArray();
        start.Release(mutationIds.Count);
        return await Task.WhenAll(applies);
    }

    // One notify reaches every replica: the wallet is credited once, one ledger row, the mutation Applied, and no
    // consumer fails — the ones after the first find the mutation applied and return.
    [Fact]
    public async Task OneMutation_AppliedByEveryReplicaAtOnce_CreditsOnce_AndNoReplicaFails()
    {
        Wallet wallet = await SeedWalletAsync();
        WalletMutation mutation = await EnqueueCreditAsync(wallet.Id, 100_000L);

        Exception?[] failures = await ApplyAtOnceAsync(Enumerable.Repeat(mutation.Id, Replicas).ToList());

        Assert.All(failures, Assert.Null);
        await using var read = pg.NewDbContext();
        Assert.Equal(100_000L, (await read.Wallets.AsNoTracking().SingleAsync(w => w.Id == wallet.Id)).AvailableAmount);
        Assert.Equal(1, await read.WalletTransactions.AsNoTracking().CountAsync(t => t.WalletId == wallet.Id));
        Assert.Equal(WalletMutationStatus.Applied,
            (await read.WalletMutations.AsNoTracking().SingleAsync(m => m.Id == mutation.Id)).Status);
    }

    // Eight replicas apply eight mutations of one wallet at once: all land, each once, and no replica fails — each waits
    // for the one before on the wallet's row instead of losing on its version and waiting for the sweeper.
    [Fact]
    public async Task MutationsOfOneWallet_AppliedOnManyReplicasAtOnce_AllLand_AndNoneFails()
    {
        Wallet wallet = await SeedWalletAsync();
        var mutations = new List<WalletMutation>();
        for (int i = 0; i < Replicas; i++)
        {
            mutations.Add(await EnqueueCreditAsync(wallet.Id, 1_000L * (i + 1)));
        }

        Exception?[] failures = await ApplyAtOnceAsync(mutations.Select(m => m.Id).ToList());

        Assert.All(failures, Assert.Null);
        await using var read = pg.NewDbContext();
        long expected = mutations.Sum(m => m.Amount);
        Assert.Equal(expected, (await read.Wallets.AsNoTracking().SingleAsync(w => w.Id == wallet.Id)).AvailableAmount);
        Assert.Equal(Replicas, await read.WalletTransactions.AsNoTracking().CountAsync(t => t.WalletId == wallet.Id));
        Assert.Equal(Replicas, await read.WalletMutations.AsNoTracking()
            .CountAsync(m => m.WalletId == wallet.Id && m.Status == WalletMutationStatus.Applied));
    }
}
