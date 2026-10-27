using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Billing.Wallet;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Auth;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The first request of a signed-in user creates their tenant, and the unique indexes on the tenant's name and its
/// owner are the only guard when two such requests overlap (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/tenancy-and-admin.md#tenant-name-taken).
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
        return new TenantResolutionMiddleware(
            tenants,
            new BootstrapTenantCommandHandler(tenants),
            new ApproveDeclaredAccountCommandHandler(tenants),
            new OpenWalletCommandHandler(new WalletRepository(db), new CreditOptions { Currency = "VND" }));
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
