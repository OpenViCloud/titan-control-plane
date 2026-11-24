using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Commands.Billing.TenantActivation;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Application.Options;
using Titan.Application.Services.Networking.Network;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Services;
using Titan.Infrastructure.Auth;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
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
        // No cluster configured: DefaultNetwork logs a warning and gives no network, so this file's counts of
        // tenants and wallets are not disturbed by the default network (titan-ops features/network/design.md#default-network).
        var defaultNetwork = new DefaultNetwork(
            new NetworkRepository(db), new NetworkRoutes(new ClusterIpv6RouteRepository(db)), new ClusterOptions(),
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), Callers.TenantMember(Guid.NewGuid())),
            NullLogger<DefaultNetwork>.Instance);
        return new TenantResolutionMiddleware(
            tenants,
            new BootstrapTenantCommandHandler(tenants),
            new ApproveDeclaredAccountCommandHandler(tenants, wallets, new TenantActivation(wallets), defaultNetwork));
    }

    private const string Block56 = "2001:41d0:d00:d400::/56";

    private static TenantResolutionMiddleware MiddlewareWithIpv6Cluster(TitanDbContext db, string clusterId)
    {
        var tenants = new TenantRepository(db);
        var wallets = new WalletRepository(db);
        var clusters = new ClusterOptions
        {
            Clusters =
            [
                new()
                {
                    Id = clusterId, Name = clusterId, Endpoint = "https://localhost:8443",
                    ServerFingerprint = new string('a', 64), OvnUplinkNetwork = "ovnup", StoragePool = "default",
                    Ipv6Block = Block56,
                },
            ],
        };
        var defaultNetwork = new DefaultNetwork(
            new NetworkRepository(db), new NetworkRoutes(new ClusterIpv6RouteRepository(db)), clusters,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), Callers.TenantMember(Guid.NewGuid())),
            NullLogger<DefaultNetwork>.Instance);
        return new TenantResolutionMiddleware(
            tenants,
            new BootstrapTenantCommandHandler(tenants),
            new ApproveDeclaredAccountCommandHandler(tenants, wallets, new TenantActivation(wallets), defaultNetwork));
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
        first.Tenants.Add(Tenant.CreatePending(name, user));
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
        Tenant pending = Tenant.CreatePending(name, user);
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

    // titan-ops features/network/design.md#default-network: the second of two first requests sees the
    // default network the first gave the tenant (HasEverHadOneAsync, read after the tenant's row lock), so only one
    // is ever created, even though both ask for one at once.
    [Fact]
    public async Task TwoFirstRequestsOfADeclaredAccount_BothSucceed_WithExactlyOneDefaultNetwork()
    {
        string user = "kc-" + Guid.NewGuid();
        string name = "decl-net-" + Guid.NewGuid().ToString("N")[..12];
        string cluster = "cluster-" + Guid.NewGuid().ToString("N")[..12];
        Tenant pending = Tenant.CreatePending(name, user);
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(pending);
            await seed.SaveChangesAsync();
            await new ClusterIpv6RouteRepository(seed).EnsureRoutesAsync(cluster);
        }

        await using TitanDbContext holder = pg.NewDbContext();
        await using var hold = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM core.tenants WHERE id = {pending.Id} FOR UPDATE");

        await using TitanDbContext firstContext = pg.NewDbContext();
        await using TitanDbContext secondContext = pg.NewDbContext();
        Task<Exception?> first = TryAsync(() => MiddlewareWithIpv6Cluster(firstContext, cluster).InvokeAsync(Request(user, name), Pass));
        Task<Exception?> second = TryAsync(() => MiddlewareWithIpv6Cluster(secondContext, cluster).InvokeAsync(Request(user, name), Pass));

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.False(first.IsCompleted || second.IsCompleted, "a request did not wait on the tenant row: no race was measured");

        await hold.RollbackAsync();
        Exception?[] outcomes = await Task.WhenAll(first, second);

        Assert.All(outcomes, Assert.Null);
        await using TitanDbContext read = pg.NewDbContext();
        List<Network> networks = await read.Networks.Where(n => n.TenantId == pending.Id).ToListAsync();
        Network only = Assert.Single(networks);
        Assert.Equal("default", only.Name.Value);
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
