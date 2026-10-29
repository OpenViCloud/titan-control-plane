using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Domain.Aggregates.Compute.SshKey;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Domain;

namespace Titan.Tests.Infrastructure.Integration;

/// The two unique indexes of core.ssh_keys are the net under two adds racing past the handler's checks
/// (titan-ops features/vm/design.md#ssh-key). InMemory enforces no index, so only Postgres shows them.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class SshKeyDbTests(PostgresFixture pg)
{
    private async Task SaveAsync(SshKey key)
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.SshKeys.Add(key);
        await db.SaveChangesAsync();
    }

    private static void AssertUniqueViolation(DbUpdateException ex) =>
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);

    [Fact]
    public async Task AKey_RoundTrips_AndIsListedForItsTenantOnly()
    {
        var tenant = Guid.NewGuid();
        SshKey key = SshKey.CreateUnchecked(tenant, "laptop", SshPublicKeyTests.Ed25519);
        await SaveAsync(key);
        await SaveAsync(SshKey.CreateUnchecked(Guid.NewGuid(), "laptop", SshPublicKeyTests.Ed25519));

        await using TitanDbContext db = pg.NewDbContext();
        var repo = new SshKeyRepository(db);
        SshKey listed = Assert.Single(await repo.ListByTenantAsync(tenant));
        Assert.Equal(key.PublicKey, listed.PublicKey);
        Assert.Equal(SshPublicKeyTests.Ed25519Fingerprint, listed.Fingerprint);
        Assert.Equal(1, await repo.CountByTenantAsync(tenant));
        Assert.True(await repo.ExistsByFingerprintAsync(tenant, SshPublicKeyTests.Ed25519Fingerprint));
    }

    [Fact]
    public async Task TheSameKeyTwiceInOneTenant_IsRefusedByTheIndex()
    {
        var tenant = Guid.NewGuid();
        await SaveAsync(SshKey.CreateUnchecked(tenant, "laptop", SshPublicKeyTests.Ed25519));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveAsync(SshKey.CreateUnchecked(tenant, "other-name", SshPublicKeyTests.Ed25519)));
        AssertUniqueViolation(ex);
    }

    [Fact]
    public async Task TheSameNameTwiceInOneTenant_IsRefusedByTheIndex()
    {
        var tenant = Guid.NewGuid();
        await SaveAsync(SshKey.CreateUnchecked(tenant, "laptop", SshPublicKeyTests.Ed25519));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(
            () => SaveAsync(SshKey.CreateUnchecked(tenant, "laptop", SshPublicKeyTests.Rsa2048)));
        AssertUniqueViolation(ex);
    }
}
