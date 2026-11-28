using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Application.Abstractions;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Application.Commands.Compute.FunctionSecret;
using Titan.Application.Commands.Compute.FunctionSecret.Contracts;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Infrastructure.FunctionSecrets;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Infrastructure.FunctionSecrets;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops features/ovc-function/design.md#function-secrets: a secret and its ciphertext
/// round-tripping the real schema, the unique name under two creates racing, the row lock that numbers two new values
/// one after the other, the versions that go with a prune or a delete, and a function version's jsonb configuration.
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class FunctionSecretDbTests(PostgresFixture pg)
{
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly AesGcmFunctionSecretCipher _cipher = AesGcmFunctionSecretCipherTests.Cipher();

    private FunctionSecret ASecret(string name = "DB_PASSWORD", string value = "s3cr3t") =>
        FunctionSecret.Create(_tenant, name, _cipher.Encrypt(_tenant, name, 1, value), "kc-member");

    private async Task SaveAsync(FunctionSecret secret)
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.FunctionSecrets.Add(secret);
        await db.SaveChangesAsync();
    }

    private async Task AddValueThroughTheHandlerAsync(string name, string value)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(_tenant);
        var handler = new AddFunctionSecretVersionCommandHandler(new FunctionSecretRepository(db), new FunctionRepository(db), _cipher,
            caller, new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new AddFunctionSecretVersionCommand(name, value));
    }

    [Fact]
    public async Task ASecret_RoundTrips_AndDecrypts()
    {
        FunctionSecret secret = ASecret();
        await SaveAsync(secret);

        await using TitanDbContext db = pg.NewDbContext();
        FunctionSecret stored = await new FunctionSecretRepository(db).FindByNameAsync(_tenant, "DB_PASSWORD") ?? throw new InvalidOperationException("gone");
        FunctionSecretVersion version = Assert.Single(stored.Versions);
        Assert.Equal("s3cr3t", _cipher.Decrypt(_tenant, stored.Name, 1, version.KeyId, version.Nonce, version.Ciphertext));
        Assert.Equal(1, await new FunctionSecretRepository(db).CountByTenantAsync(_tenant));
        Assert.Null(await new FunctionSecretRepository(db).FindByNameAsync(Guid.NewGuid(), "DB_PASSWORD"));
    }

    [Fact]
    public async Task TheSameNameTwiceInOneTenant_IsRefusedByTheIndex_AndAnotherTenantMayUseIt()
    {
        await SaveAsync(ASecret());
        await SaveAsync(FunctionSecret.Create(Guid.NewGuid(), "DB_PASSWORD", _cipher.Encrypt(_tenant, "DB_PASSWORD", 1, "x"), "kc-other"));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(ASecret()));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    // The second waits on the secret's row lock, then numbers itself after the first: versions 2 and 3, no collision.
    [Fact]
    public async Task TwoNewValues_AtOnce_AreTwoVersions_OneAfterTheOther()
    {
        await SaveAsync(ASecret());

        await Task.WhenAll(AddValueThroughTheHandlerAsync("DB_PASSWORD", "a"), AddValueThroughTheHandlerAsync("DB_PASSWORD", "b"));

        await using TitanDbContext db = pg.NewDbContext();
        FunctionSecret stored = (await new FunctionSecretRepository(db).FindByNameAsync(_tenant, "DB_PASSWORD"))!;
        Assert.Equal([1, 2, 3], stored.Versions.Select(v => v.Number).Order());
        string[] values = stored.Versions.Where(v => v.Number > 1)
            .Select(v => _cipher.Decrypt(_tenant, stored.Name, v.Number, v.KeyId, v.Nonce, v.Ciphertext)).Order().ToArray();
        Assert.Equal(["a", "b"], values);
    }

    [Fact]
    public async Task AVersionBeyondTheKept_IsDeletedFromTheTable()
    {
        await SaveAsync(ASecret());

        for (int i = 2; i <= FunctionSecret.KeptVersions + 1; i++)
        {
            await AddValueThroughTheHandlerAsync("DB_PASSWORD", $"v{i}");
        }

        await using TitanDbContext db = pg.NewDbContext();
        Guid id = (await db.FunctionSecrets.AsNoTracking().SingleAsync(s => s.TenantId == _tenant)).Id;
        List<int> numbers = await db.Database
            .SqlQuery<int>($"SELECT number AS \"Value\" FROM core.function_secret_versions WHERE secret_id = {id} ORDER BY number")
            .ToListAsync();
        Assert.Equal(Enumerable.Range(2, FunctionSecret.KeptVersions), numbers);
    }

    [Fact]
    public async Task DeletingASecret_TakesItsVersions()
    {
        FunctionSecret secret = ASecret();
        await SaveAsync(secret);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            ICurrentUser caller = Callers.TenantMember(_tenant);
            var handler = new DeleteFunctionSecretCommandHandler(new FunctionSecretRepository(db), new FunctionRepository(db),
                caller, new ActivityRecorder(new ActivityEventRepository(db), caller));
            await handler.HandleAsync(new DeleteFunctionSecretCommand("DB_PASSWORD"));
        }

        await using TitanDbContext read = pg.NewDbContext();
        int left = await read.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM core.function_secret_versions WHERE secret_id = {secret.Id}")
            .SingleAsync();
        Assert.Equal(0, left);
        Assert.Null(await new FunctionSecretRepository(read).FindByNameAsync(_tenant, "DB_PASSWORD"));
    }

    [Fact]
    public async Task AFunctionVersionsConfiguration_RoundTripsAsJsonb()
    {
        Function function = TestFunctions.AFunction(_tenant, name: $"fn-{Guid.NewGuid():N}"[..12]);
        Guid secretId = Guid.NewGuid();
        FunctionVersion version = function.AddVersion(TestFunctions.Spec(function,
            env: new Dictionary<string, string> { ["LOG_LEVEL"] = "info", ["apiBase"] = "é" },
            secretRefs: [FunctionSecretRef.Of("DB", secretId, "DB_PASSWORD", 2)],
            schedule: "0 * * * *"));
        FunctionVersion bare = function.WithVersion();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Functions.Add(function);
            await db.SaveChangesAsync();
        }

        await using TitanDbContext read = pg.NewDbContext();
        Function stored = (await new FunctionRepository(read).FindByIdAsync(function.Id))!;
        FunctionVersion again = stored.Versions.Single(v => v.Id == version.Id);
        Assert.Equal(new Dictionary<string, string> { ["LOG_LEVEL"] = "info", ["apiBase"] = "é" }, again.Env);
        Assert.Equal([FunctionSecretRef.Of("DB", secretId, "DB_PASSWORD", 2)], again.SecretRefs);
        Assert.Equal("0 * * * *", again.Schedule);
        FunctionVersion none = stored.Versions.Single(v => v.Id == bare.Id);
        Assert.Empty(none.Env);
        Assert.Empty(none.SecretRefs);
        Assert.Null(none.Schedule);
        string kind = await read.Database
            .SqlQuery<string>($"SELECT jsonb_typeof(secret_refs) AS \"Value\" FROM core.function_versions WHERE id = {version.Id}")
            .SingleAsync();
        Assert.Equal("array", kind);
    }
}
