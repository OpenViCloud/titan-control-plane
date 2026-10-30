using Titan.Domain.Aggregates.Wallet;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

// wallets.version catches a lost update at commit (titan-ops architecture/money-and-wallet.md#wallet-one-balance): two
// writers read the same version, the second UPDATE ... AND version = N matches no row. WalletSchemaTests pins that the
// token is declared; this shows Postgres enforcing it. Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class WalletVersionDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task TwoWritersOfTheSameVersion_TheSecondIsAConflict_AndOnlyTheFirstDebitLands()
    {
        var tenantId = Guid.NewGuid();
        await using (var seed = pg.NewDbContext())
        {
            var wallet = Wallet.Open(tenantId, "VND");
            wallet.Credit(100_000L);
            seed.Wallets.Add(wallet);
            await seed.SaveChangesAsync();
        }

        await using var first = pg.NewDbContext();
        await using var second = pg.NewDbContext();
        var firstRepo = new WalletRepository(first);
        var secondRepo = new WalletRepository(second);
        var firstCopy = await firstRepo.GetByTenantForUpdateAsync(tenantId, CancellationToken.None);
        var secondCopy = await secondRepo.GetByTenantForUpdateAsync(tenantId, CancellationToken.None);
        Assert.NotNull(firstCopy);
        Assert.NotNull(secondCopy);

        firstCopy.Debit(30_000L);
        secondCopy.Debit(30_000L);
        await firstRepo.SaveChangesAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => secondRepo.SaveChangesAsync(CancellationToken.None));

        await using var read = pg.NewDbContext();
        long balance = await new WalletRepository(read).GetBalanceAsync(tenantId, CancellationToken.None);
        Assert.Equal(70_000L, balance);
    }
}
