using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using Titan.Application.Queries.Platform.BackupTarget;
using Titan.Application.Queries.Platform.BackupTarget.Contracts;
using Titan.Domain.Aggregates.Platform.BackupTarget;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Backup;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The backup target through the real handlers, repositories and migrations on Postgres: one row under a fixed id, a secret
/// that is only ever ciphertext in the table and never in a response or the audit trail
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/design.md#backup-target). The table holds a single row, so each test starts by emptying it
/// (the tests of one collection run one after another).
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class BackupTargetDbTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Endpoint = "https://acct.r2.cloudflarestorage.com";
    private const string SecretValue = "S3CR3T-access-key-value-0123456789";
    private const string AdminId = "kc-backup-admin";

    private static string Key(byte seed) =>
        Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)(i + seed)).ToArray());

    private static ClusterKeyBackupSecretProtector Protector(byte keySeed = 0) =>
        new(new AesGcmClusterCredentialCipher(Options.Create(new ClusterEncryptionOptions { ClusterKey = Key(keySeed) })));

    public async Task InitializeAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        await db.BackupTargets.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SetAsync(SetBackupTargetCommand command, byte keySeed = 0)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var handler = new SetBackupTargetCommandHandler(
            new BackupTargetRepository(db), Protector(keySeed), new AdminAuditLogRepository(db), Callers.Admin(AdminId));
        await handler.HandleAsync(command);
    }

    private async Task<BackupTargetQueryResult> GetAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await new GetBackupTargetQueryHandler(new BackupTargetRepository(db)).HandleAsync(new GetBackupTargetQuery());
    }

    private static SetBackupTargetCommand Command(string bucket, string? secret = SecretValue, string accessKeyId = "key-id") =>
        new(Endpoint, "auto", bucket, accessKeyId, secret);

    private static string NewBucket() => "b-" + Guid.NewGuid().ToString("N");

    private async Task<List<Titan.Domain.Aggregates.Platform.AdminAuditLog.AdminAuditLog>> AuditRowsAboutAsync(string bucket)
    {
        await using TitanDbContext db = pg.NewDbContext();
        var rows = await db.AdminAuditLogs.AsNoTracking()
            .Where(a => a.Action == SetBackupTargetCommandHandler.SetAction && a.Detail!.Contains(bucket))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
        return rows;
    }

    [Fact]
    public async Task GetBeforeAnySet_IsNotFound_WithItsOwnCode()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(GetAsync);

        Assert.Equal("BACKUP_TARGET_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task ThePutFollowedByTheGet_ReturnsTheFields_AndNoSecret()
    {
        var bucket = NewBucket();
        await SetAsync(Command(bucket));

        var result = await GetAsync();

        Assert.Equal(Endpoint, result.Endpoint);
        Assert.Equal("auto", result.Region);
        Assert.Equal(bucket, result.Bucket);
        Assert.Equal("key-id", result.AccessKeyId);
        Assert.NotEqual(default, result.UpdatedAt);
        Assert.DoesNotContain(SecretValue, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task TheSecretColumn_HoldsCiphertextOnly_ThatTheClusterKeyOpens()
    {
        await SetAsync(Command(NewBucket()));

        await using TitanDbContext db = pg.NewDbContext();
        var stored = await db.BackupTargets.AsNoTracking().SingleAsync();

        Assert.DoesNotContain(SecretValue, Encoding.UTF8.GetString(stored.SecretCipher));
        Assert.Equal(SecretValue, Protector().Unprotect(stored.SecretCipher));
    }

    [Fact]
    public async Task ThePutIsAnUpsert_OneRowAfterTwo()
    {
        await SetAsync(Command(NewBucket()));
        var second = NewBucket();
        await SetAsync(Command(second));

        await using TitanDbContext db = pg.NewDbContext();
        Assert.Equal(1, await db.BackupTargets.CountAsync());
        Assert.Equal(second, (await GetAsync()).Bucket);
    }

    [Fact]
    public async Task APutWithoutASecret_KeepsTheStoredOne_AndChangesTheRest()
    {
        await SetAsync(Command(NewBucket()));
        var other = NewBucket();

        await SetAsync(Command(other, secret: null, accessKeyId: "other-key"));

        await using TitanDbContext db = pg.NewDbContext();
        var stored = await db.BackupTargets.AsNoTracking().SingleAsync();
        Assert.Equal(SecretValue, Protector().Unprotect(stored.SecretCipher));
        Assert.Equal(other, stored.Bucket);
        Assert.Equal("other-key", stored.AccessKeyId);
    }

    [Fact]
    public async Task APutWithASecret_ReplacesTheStoredOne()
    {
        await SetAsync(Command(NewBucket()));

        await SetAsync(Command(NewBucket(), secret: "ANOTHER-secret-value"));

        await using TitanDbContext db = pg.NewDbContext();
        var stored = await db.BackupTargets.AsNoTracking().SingleAsync();
        Assert.Equal("ANOTHER-secret-value", Protector().Unprotect(stored.SecretCipher));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task TheFirstPutWithoutASecret_IsRefused_AndStoresNothing(string? secret)
    {
        var bucket = NewBucket();

        var ex = await Assert.ThrowsAsync<BusinessException>(() => SetAsync(Command(bucket, secret)));

        Assert.Equal("BACKUP_TARGET_SECRET_REQUIRED", ex.Code);
        await using TitanDbContext db = pg.NewDbContext();
        Assert.Equal(0, await db.BackupTargets.CountAsync());
        Assert.Empty(await AuditRowsAboutAsync(bucket));
    }

    [Fact]
    public async Task EachPut_LeavesOneAuditRow_WithTheActor_AndNeverTheSecret()
    {
        var bucket = NewBucket();
        await SetAsync(Command(bucket));
        await SetAsync(Command(bucket, secret: null));

        var rows = await AuditRowsAboutAsync(bucket);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(AdminId, r.ActorKeycloakUserId);
            Assert.Equal(SetBackupTargetCommandHandler.ResourceType, r.ResourceType);
            Assert.Equal(Guid.Empty, r.OwnerTenantId);
            Assert.DoesNotContain(SecretValue, r.Detail!);
        });
        Assert.Contains("secret=changed", rows[0].Detail);
        Assert.Contains("secret=kept", rows[1].Detail);
    }

    // The audit line is built from what the row holds, so the trail and the table never disagree about the endpoint.
    [Fact]
    public async Task TheAuditDetail_NamesTheEndpointAsStored_NotAsTyped()
    {
        var bucket = NewBucket();
        await SetAsync(new SetBackupTargetCommand(Endpoint + "/", "auto", bucket, "key-id", SecretValue));

        var row = Assert.Single(await AuditRowsAboutAsync(bucket));

        Assert.Contains("endpoint=" + Endpoint + ";", row.Detail);
    }

    // Every field at its maximum: the audit line must still fit its column, so the PUT is never a 500 for length.
    [Fact]
    public async Task EveryFieldAtItsMaximum_IsStored_AndAudited()
    {
        string host = new('a', BackupTarget.MaxEndpointLength - "https://.io".Length);
        string longBucket = "b" + new string('c', BackupTarget.MaxBucketLength - 1);
        var command = new SetBackupTargetCommand($"https://{host}.io", new string('r', 64), longBucket, new string('k', 128),
            new string('s', BackupTarget.MaxSecretLength));

        await SetAsync(command);

        Assert.Equal(longBucket, (await GetAsync()).Bucket);
        Assert.Single(await AuditRowsAboutAsync(longBucket));
    }

    // Two admins saving the first target at once: the second is told to send it again, nothing partial is written.
    [Fact]
    public async Task TwoFirstSavesAtOnce_TheSecondIsToldToSendItAgain()
    {
        await using TitanDbContext first = pg.NewDbContext();
        await using TitanDbContext second = pg.NewDbContext();
        var firstRepo = new BackupTargetRepository(first);
        var secondRepo = new BackupTargetRepository(second);
        Assert.Null(await firstRepo.FindAsync());
        Assert.Null(await secondRepo.FindAsync());
        var cipher = Protector().Protect(SecretValue);
        firstRepo.Add(BackupTarget.Create(Endpoint, "auto", NewBucket(), "key-id", cipher));
        secondRepo.Add(BackupTarget.Create(Endpoint, "auto", NewBucket(), "key-id", cipher));

        await firstRepo.SaveAsync();
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(() => secondRepo.SaveAsync());

        Assert.Equal("BACKUP_TARGET_CONFLICT", ex.Code);
        await using TitanDbContext db = pg.NewDbContext();
        Assert.Equal(1, await db.BackupTargets.CountAsync());
    }

    // The key that sealed the secret is Encryption__ClusterKey; a different one cannot open it, loudly.
    [Fact]
    public async Task AnotherClusterKey_CannotOpenTheStoredSecret()
    {
        await SetAsync(Command(NewBucket()));

        await using TitanDbContext db = pg.NewDbContext();
        var stored = await db.BackupTargets.AsNoTracking().SingleAsync();

        Assert.ThrowsAny<Exception>(() => Protector(keySeed: 7).Unprotect(stored.SecretCipher));
    }
}
