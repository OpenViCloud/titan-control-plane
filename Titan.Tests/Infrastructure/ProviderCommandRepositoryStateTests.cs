using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The claim query is the SQL of ProviderCommand.IsClaimable (titan-ops architecture/domain-methods.md#plain-code): every
// shape a row can be in goes through both, so the two cannot drift apart.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it); the race of two
// workers on one row is ProviderCommandLeaseDbTests.
public class ProviderCommandRepositoryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"provider-command-state-{Guid.NewGuid()}")
            .Options);

    private static ProviderCommand Pending(string key, DateTimeOffset createdAt) =>
        ProviderCommand.Create(key, ProviderCommandKinds.VmDelete, "mock", "{}", createdAt);

    private static ProviderCommand Leased(string key, DateTimeOffset leasedAt)
    {
        ProviderCommand row = Pending(key, leasedAt);
        row.Lease(Guid.NewGuid(), leasedAt);
        return row;
    }

    // Every shape, named by its key; the key is what the assertions list.
    private static List<ProviderCommand> EveryShape()
    {
        ProviderCommand done = Leased("done", Now.AddHours(-1));
        done.Complete(done.LeaseId!.Value);
        ProviderCommand failed = Leased("failed", Now.AddHours(-1));
        failed.Fail(failed.LeaseId!.Value, "gave up", retryable: false, Now.AddHours(-1));
        ProviderCommand retryWaiting = Leased("retry-waiting", Now);
        retryWaiting.Fail(retryWaiting.LeaseId!.Value, "try again", retryable: true, Now);

        return
        [
            Pending("pending-due", Now.AddMinutes(-1)),
            Pending("pending-now", Now),
            Pending("pending-later", Now.AddMinutes(5)),
            Leased("lease-ran-out", Now.AddHours(-1)),
            Leased("lease-runs-out-now", Now - ProviderCommand.LeaseLength),
            Leased("lease-held", Now),
            done, failed, retryWaiting,
        ];
    }

    [Fact]
    public async Task Claimable_IsAPendingRowThatIsDue_OrALeaseThatRanOut()
    {
        await using TitanDbContext db = NewDb();
        db.ProviderCommands.AddRange(EveryShape());
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync([ProviderCommandKinds.VmDelete], Now, 100, default);

        Assert.Equal(["lease-ran-out", "lease-runs-out-now", "pending-due", "pending-now"],
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ClaimQuery_AgreesWithIsClaimable_ForEveryShape()
    {
        await using TitanDbContext db = NewDb();
        List<ProviderCommand> shapes = EveryShape();
        db.ProviderCommands.AddRange(shapes);
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync([ProviderCommandKinds.VmDelete], Now, 100, default);

        Assert.Equal(shapes.Where(c => c.IsClaimable(Now)).Select(c => c.Key).Order(StringComparer.Ordinal),
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }
}
