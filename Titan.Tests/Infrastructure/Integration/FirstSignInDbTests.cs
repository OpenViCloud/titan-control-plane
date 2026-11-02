using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Billing.TenantActivation;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Services;
using Titan.Infrastructure.Auth;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The first request of a signed-in user creates their tenant, and the unique indexes on the tenant's name and its
/// owner are the guard when two such requests create it (titan-ops architecture/tenancy-and-admin.md#tenant-name-taken);
/// approving a declared account takes turns on the tenant's row lock instead.
/// InMemory enforces no index, so only Postgres shows them. A request under test runs the real middleware on its own
/// context, as the host does per request.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class FirstSignInDbTests(PostgresFixture pg)
{
    private static TenantResolutionMiddleware Middleware(TitanDbContext db)
    {
        var tenants = new TenantRepository(db);
        var wallets = new WalletRepository(db);
        return new TenantResolutionMiddleware(
            tenants,
            new BootstrapTenantCommandHandler(tenants),
            new ApproveDeclaredAccountCommandHandler(tenants, wallets, new TenantActivation(wallets),
                new CreditOptions { Currency = "VND" }));
    }

    private static DefaultHttpContext Request(string keycloakUserId, string username)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, keycloakUserId),
            new Claim("preferred_username", username),
            new Claim("email", username + "@example.com"),
            new Claim(ClaimTypes.Role, TenantResolutionMiddleware.AutoApproveRole),
        }, authenticationType: "test"));
        return ctx;
    }

    private static Task Pass(HttpContext _) => Task.CompletedTask;

    private async Task<int> CountTenantsNamedAsync(string name)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await db.Tenants.CountAsync(t => t.Name == name);
    }

    // The first request is stood in for by its tenant insert, held open in a transaction while the second request of
    // the same user runs: the second waits on the index, then loses once the first commits. It must still pass, onto
    // the tenant the first made.
    [Fact]
    public async Task TheSameUsersSecondFirstRequest_LosingTheInsertRace_PassesOntoTheOneTenant()
    {
        string user = "kc-" + Guid.NewGuid();
        string name = "first-" + Guid.NewGuid().ToString("N")[..12];

        await using TitanDbContext first = pg.NewDbContext();
        await using var firstInsert = await first.Database.BeginTransactionAsync();
        first.Tenants.Add(Tenant.CreatePending(name, user, name + "@example.com"));
        await first.SaveChangesAsync();

        await using TitanDbContext second = pg.NewDbContext();
        DefaultHttpContext secondRequest = Request(user, name);
        bool passed = false;
        Task secondRun = Middleware(second).InvokeAsync(secondRequest, _ => { passed = true; return Task.CompletedTask; });

        // The race is real only if the second insert is waiting on the first's open row.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(secondRun.IsCompleted, "the second request did not wait on the first insert: no race was measured");

        await firstInsert.CommitAsync();
        await secondRun;

        Assert.True(passed);
        Assert.Equal(1, await CountTenantsNamedAsync(name));
        await using TitanDbContext read = pg.NewDbContext();
        Tenant tenant = await read.Tenants.Include(t => t.Members).SingleAsync(t => t.Name == name);
        Assert.Equal(user, Assert.Single(tenant.Members).KeycloakUserId);
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.Equal(1, await read.Wallets.CountAsync(w => w.TenantId == tenant.Id));
        Assert.Equal(tenant.Id, secondRequest.Items["TenantId"]);
    }

    // Two first requests of a declared account, its tenant already pending: the approval and its wallet commit in one
    // save under the tenant's row lock (titan-ops architecture/domain-services.md#cross-aggregate-lock), so the second
    // waits, then finds the tenant Active with its wallet. A third context holds the row first, so both are queued
    // before either reads: without the lock both read it pending, both add a wallet, and the second loses on
    // UNIQUE(tenant_id).
    [Fact]
    public async Task TwoFirstRequestsOfADeclaredAccount_TakeTurns_OneWallet_AndBothPass()
    {
        string user = "kc-" + Guid.NewGuid();
        string name = "decl-" + Guid.NewGuid().ToString("N")[..12];
        Tenant pending = Tenant.CreatePending(name, user, name + "@example.com");
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(pending);
            await seed.SaveChangesAsync();
        }

        await using TitanDbContext holder = pg.NewDbContext();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM core.tenants WHERE id = {pending.Id} FOR UPDATE");

        await using TitanDbContext firstContext = pg.NewDbContext();
        await using TitanDbContext secondContext = pg.NewDbContext();
        int passed = 0;
        Task<Exception?> first = TryAsync(() => Middleware(firstContext).InvokeAsync(Request(user, name), _ => { Interlocked.Increment(ref passed); return Task.CompletedTask; }));
        Task<Exception?> second = TryAsync(() => Middleware(secondContext).InvokeAsync(Request(user, name), _ => { Interlocked.Increment(ref passed); return Task.CompletedTask; }));

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(first.IsCompleted || second.IsCompleted, "a request did not wait on the tenant row: no race was measured");

        await hold.RollbackAsync();
        Exception?[] outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, Assert.Null);
        Assert.Equal(2, passed);
        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(TenantStatus.Active, (await read.Tenants.SingleAsync(t => t.Id == pending.Id)).Status);
        Assert.Equal(1, await read.Wallets.CountAsync(w => w.TenantId == pending.Id));
    }

    private static async Task<Exception?> TryAsync(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    // Another user already holds the name: every request of the newcomer is refused with TENANT_NAME_TAKEN, and no
    // second tenant appears.
    [Fact]
    public async Task ANameAnotherUserHolds_RefusesEveryRequestOfTheNewcomer_WithTenantNameTaken()
    {
        string name = "taken-" + Guid.NewGuid().ToString("N")[..12];
        string holder = "kc-" + Guid.NewGuid();
        string newcomer = "kc-" + Guid.NewGuid();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await Middleware(db).InvokeAsync(Request(holder, name), Pass);
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            await using TitanDbContext db = pg.NewDbContext();
            var ex = await Assert.ThrowsAsync<ConflictException>(() => Middleware(db).InvokeAsync(Request(newcomer, name), Pass));
            Assert.Equal("TENANT_NAME_TAKEN", ex.Code);
        }

        Assert.Equal(1, await CountTenantsNamedAsync(name));
        await using TitanDbContext read = pg.NewDbContext();
        Assert.False(await read.Set<Member>().AnyAsync(m => m.KeycloakUserId == newcomer));
    }
}
