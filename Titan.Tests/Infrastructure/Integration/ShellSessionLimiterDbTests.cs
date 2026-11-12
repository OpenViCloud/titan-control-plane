using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Domain.Aggregates.Tenant;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Integration;

// titan-ops features/vm/design.md#ssh-session on real Postgres: replicas opening sessions for one tenant at once take
// turns on the tenant's row, so the count they read is never stale and the cap holds.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ShellSessionLimiterDbTests(PostgresFixture pg)
{
    private const int Replicas = 16;
    private const int Rounds = 20;

    private ShellSessionLimiter NewReplica(ServiceProvider services) =>
        new(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<ShellSessionLimiter>.Instance);

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TitanDbContext>(o => o.UseNpgsql(pg.ConnectionString));
        return services.BuildServiceProvider();
    }

    private async Task<Guid> SeedTenantAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        Tenant tenant = Tenant.CreatePending("ssh-" + Guid.NewGuid().ToString("N")[..12], "kc-" + Guid.NewGuid());
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    // Four places taken, sixteen replicas ask for the fifth at once, twenty times over: each time one gets it. Without
    // the lock on the tenant's row several read four and insert, and the tenant ends up with more than five.
    [Fact]
    public async Task ReplicasOpeningAtOnce_ForTheLastPlace_OneGetsIt()
    {
        await using ServiceProvider services = Services();
        for (int round = 0; round < Rounds; round++)
        {
            Guid tenant = await SeedTenantAsync();
            ShellSessionLimiter first = NewReplica(services);
            var taken = new List<IAsyncDisposable?>();
            for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant - 1; i++)
            {
                taken.Add(await first.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
            }
            Assert.All(taken, Assert.NotNull);
            using var start = new SemaphoreSlim(0, Replicas);

            Task<IAsyncDisposable?>[] opens = Enumerable.Range(0, Replicas).Select(async _ =>
            {
                ShellSessionLimiter replica = NewReplica(services);
                await start.WaitAsync();
                return await replica.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None);
            }).ToArray();
            start.Release(Replicas);
            IAsyncDisposable?[] places = await Task.WhenAll(opens);

            await using TitanDbContext read = pg.NewDbContext();
            Assert.Equal(ShellSessionLimiter.MaxSessionsPerTenant, await read.ShellSessions.CountAsync(s => s.TenantId == tenant));
            Assert.Single(places, p => p != null);
            foreach (IAsyncDisposable? place in places.Concat(taken))
            {
                if (place != null)
                {
                    await place.DisposeAsync();
                }
            }
        }
    }

    // Closing deletes the row on Postgres too, and a row a dead replica left is not counted.
    [Fact]
    public async Task AClosedSession_LeavesNoRow_AndADeadReplicasRow_HoldsNoPlace()
    {
        Guid tenant = await SeedTenantAsync();
        await using ServiceProvider services = Services();
        ShellSessionLimiter replica = NewReplica(services);
        IAsyncDisposable? closed = await replica.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None);
        await closed!.DisposeAsync();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            DateTimeOffset longAgo = DateTimeOffset.UtcNow - ShellSessionLimiter.GoneAfter - TimeSpan.FromMinutes(1);
            for (int i = 0; i < ShellSessionLimiter.MaxSessionsPerTenant; i++)
            {
                db.ShellSessions.Add(new ShellSession
                {
                    Id = Guid.NewGuid(), TenantId = tenant, VmId = Guid.NewGuid(), OpenedAt = longAgo, SeenAt = longAgo,
                });
            }
            await db.SaveChangesAsync();
        }

        IAsyncDisposable? place = await replica.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(place);
        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(1, await read.ShellSessions.CountAsync(s => s.TenantId == tenant));
    }
}
