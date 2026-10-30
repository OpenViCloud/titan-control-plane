using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Domain;

namespace Titan.Tests.Infrastructure.Integration;

/// The two unique indexes of core.api_tokens are the net under two creates racing past the handler's checks
/// (titan-ops features/public-api/design.md#api-token). InMemory enforces no index, so only Postgres shows them.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ApiTokenDbTests(PostgresFixture pg)
{
    private const string OtherSecret = "ovc_Zz39Xk2LmP0aZ8yT4rWbN6cJ1dF5gH7sE9uIoK2pQwA";

    private static ApiToken New(Guid tenantId, string name, string secret, int? days = 90) =>
        ApiToken.Create(tenantId, "kc-1", 0, name, secret, days, ApiTokenTests.Now);

    // Every test makes its own secret: the hash index spans all tenants, and the database outlives one test.
    private static string FreshSecret() =>
        "ovc_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task SaveAsync(ApiToken token)
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.ApiTokens.Add(token);
        await db.SaveChangesAsync();
    }

    private static void AssertUniqueViolation(DbUpdateException ex) =>
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);

    [Fact]
    public async Task AToken_RoundTrips_IsFoundByItsHash_AndIsListedForItsTenantOnly()
    {
        var tenant = Guid.NewGuid();
        string secret = FreshSecret();
        ApiToken token = New(tenant, "terraform", secret, days: null);
        await SaveAsync(token);
        await SaveAsync(New(Guid.NewGuid(), "terraform", FreshSecret()));

        await using TitanDbContext db = pg.NewDbContext();
        var repo = new ApiTokenRepository(db);
        ApiToken listed = Assert.Single(await repo.ListByTenantAsync(tenant));
        Assert.Equal(token.Prefix, listed.Prefix);
        Assert.Null(listed.ExpiresAt);
        Assert.Equal(1, await repo.CountByTenantAsync(tenant));
        Assert.True(await repo.ExistsByNameAsync(tenant, "terraform"));

        ApiToken? found = await repo.FindBySecretHashAsync(ApiToken.HashOf(secret));
        Assert.Equal(token.Id, found?.Id);
        Assert.Null(await repo.FindBySecretHashAsync(ApiToken.HashOf(OtherSecret)));
    }

    [Fact]
    public async Task TheSameSecretTwice_EvenAcrossTenants_IsRefusedByTheIndex()
    {
        string secret = FreshSecret();
        await SaveAsync(New(Guid.NewGuid(), "a", secret));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(New(Guid.NewGuid(), "b", secret)));
        AssertUniqueViolation(ex);
    }

    [Fact]
    public async Task TheSameNameTwiceInOneTenant_IsRefusedByTheIndex()
    {
        var tenant = Guid.NewGuid();
        await SaveAsync(New(tenant, "terraform", FreshSecret()));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(New(tenant, "terraform", FreshSecret())));
        AssertUniqueViolation(ex);
    }
}
