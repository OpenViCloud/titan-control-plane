using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops features/vm/design.md#vm-console on real Postgres: a ticket is taken by one DELETE … RETURNING, so replicas
// that take the same ticket at once open one console between them.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ConsoleTicketDbTests(PostgresFixture pg)
{
    private const int Replicas = 16;
    private const int Rounds = 20;

    private static string NewTicket() =>
        "ovct_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')[..43];

    [Fact]
    public async Task ATicket_IsTakenWithItsRow_AndOnlyOnce()
    {
        string ticket = NewTicket();
        Guid vm = Guid.NewGuid();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await new ConsoleTicketStore(db, TimeProvider.System).IssueAsync(Guid.NewGuid(), vm, "kc-user", ticket, CancellationToken.None);
        }

        ConsoleTicket? first;
        ConsoleTicket? second;
        await using (TitanDbContext db = pg.NewDbContext())
        {
            first = await new ConsoleTicketStore(db, TimeProvider.System).TakeAsync(ticket, CancellationToken.None);
            second = await new ConsoleTicketStore(db, TimeProvider.System).TakeAsync(ticket, CancellationToken.None);
        }

        Assert.NotNull(first);
        Assert.Equal(vm, first.VmId);
        Assert.Equal("kc-user", first.CreatedBy);
        Assert.Null(second);
        await using TitanDbContext check = pg.NewDbContext();
        Assert.False(await check.ConsoleTickets.AnyAsync(t => t.TokenHash == ConsoleTicketStore.HashOf(ticket)));
    }

    // Sixteen replicas, their connections open, take one ticket at the same instant, twenty times over: each time one
    // console opens. A read-then-delete lets several read the row before any delete commits, and all but one of their
    // deletes then fail.
    [Fact]
    public async Task ReplicasTakingOneTicketAtOnce_OneGetsIt()
    {
        for (int round = 0; round < Rounds; round++)
        {
            string ticket = NewTicket();
            await using (TitanDbContext db = pg.NewDbContext())
            {
                await new ConsoleTicketStore(db, TimeProvider.System).IssueAsync(Guid.NewGuid(), Guid.NewGuid(), "kc-user", ticket, CancellationToken.None);
            }
            using var start = new SemaphoreSlim(0, Replicas);

            Task<ConsoleTicket?>[] takes = Enumerable.Range(0, Replicas).Select(async _ =>
            {
                await using TitanDbContext db = pg.NewDbContext();
                await db.Database.OpenConnectionAsync();
                var store = new ConsoleTicketStore(db, TimeProvider.System);
                await start.WaitAsync();
                return await store.TakeAsync(ticket, CancellationToken.None);
            }).ToArray();
            start.Release(Replicas);
            ConsoleTicket?[] taken = await Task.WhenAll(takes);

            Assert.Single(taken, t => t != null);
        }
    }

    // The same ticket sent again (a retry without Idempotency-Key) is not an error, and the first row stays.
    [Fact]
    public async Task TheSameTicketIssuedTwice_KeepsTheFirstRow_WithoutAnError()
    {
        string ticket = NewTicket();
        Guid vm = Guid.NewGuid();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            var store = new ConsoleTicketStore(db, TimeProvider.System);
            await store.IssueAsync(Guid.NewGuid(), vm, "kc-user", ticket, CancellationToken.None);
            await store.IssueAsync(Guid.NewGuid(), Guid.NewGuid(), "kc-other", ticket, CancellationToken.None);
        }

        await using TitanDbContext check = pg.NewDbContext();
        ConsoleTicket row = await check.ConsoleTickets.SingleAsync(t => t.TokenHash == ConsoleTicketStore.HashOf(ticket));
        Assert.Equal(vm, row.VmId);
        Assert.Equal("kc-user", row.CreatedBy);
    }
}
