using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Commands.Platform.Backup.Contracts;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Commands.Platform.BackupTarget.Contracts;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Infrastructure.Backup;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure.Integration;

/// The backup cache through the real handlers, repositories and migrations on Postgres: rows written from the bucket, rebuilt
/// from it after the tables are emptied, and no wallet or ledger row touched by any of it
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/user-stories.md#bak-03).
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class BackupStatusDbTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryBackupBucket _bucket = new();

    private static ClusterKeyBackupSecretProtector Protector() =>
        new(new AesGcmClusterCredentialCipher(Options.Create(new ClusterEncryptionOptions
        {
            ClusterKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        })));

    public async Task InitializeAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        await db.Backups.ExecuteDeleteAsync();
        await db.BackupObservations.ExecuteDeleteAsync();
        await db.BackupTargets.ExecuteDeleteAsync();

        await using TitanDbContext setDb = pg.NewDbContext();
        await new SetBackupTargetCommandHandler(
            new BackupTargetRepository(setDb), Protector(), new AdminAuditLogRepository(setDb), Callers.Admin("kc-backup-status"))
            .HandleAsync(new SetBackupTargetCommand("https://acct.r2.cloudflarestorage.com", "auto", "titan-backup-dev", "key-id", "S3CR3T-value"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private void Put(string key, string content, DateTimeOffset modified, long? size = null)
    {
        _bucket.Objects[key] = size is null ? Encoding.UTF8.GetBytes(content) : new byte[size.Value];
        _bucket.Modified[key] = modified;
    }

    private void SeedBucket()
    {
        Put(BackupLayout.BasePrefix + "20261024T140631/backup.info", "status=DONE\nend_time=2026-10-24 14:06:33+00:00\n", Now.AddHours(-22));
        Put(BackupLayout.BasePrefix + "20261024T140631/data.tar", "", Now.AddHours(-22), 5000);
        Put(BackupLayout.BasePrefix + "20261025T100000/data.tar", "", Now.AddMinutes(-20), 700);
        Put(BackupLayout.WalPrefix + "0000000100000000/A1.gz", "w", Now.AddMinutes(-4));
    }

    private async Task RefreshAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        await new RefreshBackupStatusCommandHandler(
            new BackupTargetRepository(db), Protector(), new FixedBackupBucketFactory(_bucket),
            new BackupRecordRepository(db), new BackupObservationRepository(db), new FixedClock(Now))
            .HandleAsync(new RefreshBackupStatusCommand());
    }

    private async Task<BackupsQueryResult> ListAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await new ListBackupsQueryHandler(new BackupRecordRepository(db), new BackupObservationRepository(db), new FixedClock(Now))
            .HandleAsync(new ListBackupsQuery());
    }

    private async Task<List<(string Key, BackupRecordStatus Status, DateTimeOffset TakenAt, long Size)>> RowsAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await db.Backups.AsNoTracking().ToListAsync())
            .OrderBy(r => r.BackupKey, StringComparer.Ordinal)
            .Select(r => (r.BackupKey, r.Status, r.TakenAt, r.SizeBytes))
            .ToList();
    }

    [Fact]
    public async Task TheRefresh_WritesTheRowsAndTheObservation_ThePageReadsThem()
    {
        SeedBucket();

        await RefreshAsync();

        var rows = await RowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(BackupRecordStatus.Complete, rows[0].Status);
        Assert.Equal(BackupRecordStatus.Incomplete, rows[1].Status);
        var item = Assert.Single((await ListAsync()).Items);
        Assert.Equal(BackupHealth.Ok, item.Health);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 14, 6, 33, TimeSpan.Zero), item.TakenAt);
        Assert.Equal(Now.AddMinutes(-4), item.LastWalAt);
        Assert.Equal(Now, (await ListAsync()).ReadAt);
    }

    [Fact]
    public async Task TwoRefreshes_LeaveOneRowPerBackup()
    {
        SeedBucket();

        await RefreshAsync();
        await RefreshAsync();

        Assert.Equal(2, (await RowsAsync()).Count);
    }

    [Fact]
    public async Task AfterTheTablesAreEmptied_TheNextRefreshBringsBackTheSameRows()
    {
        SeedBucket();
        await RefreshAsync();
        var before = await RowsAsync();

        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Backups.ExecuteDeleteAsync();
            await db.BackupObservations.ExecuteDeleteAsync();
        }
        await RefreshAsync();

        Assert.Equal(before, await RowsAsync());
    }

    [Fact]
    public async Task AnUnreadableBucket_KeepsTheRows_AndThePageSaysUnknown()
    {
        SeedBucket();
        await RefreshAsync();
        _bucket.OnListObjects = _ => throw new Titan.Application.Abstractions.BackupBucketException("access denied (AccessDenied)");

        await RefreshAsync();

        Assert.Equal(2, (await RowsAsync()).Count);
        var item = Assert.Single((await ListAsync()).Items);
        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.BucketUnreadable), (item.Health, item.Reason));
        await using TitanDbContext db = pg.NewDbContext();
        var observation = await db.BackupObservations.AsNoTracking().SingleAsync();
        Assert.Equal(BackupReadOutcome.Unreadable, observation.Outcome);
        Assert.Equal(Now.AddMinutes(-4), observation.LastWalAt);
    }

    [Fact]
    public async Task TwoRowsForTheSameBackup_AreRefusedByTheDatabase()
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "K1", Now, 1, BackupRecordStatus.Complete, Now));
        db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "K1", Now, 1, BackupRecordStatus.Complete, Now));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    // Backing up and restoring are the platform's duty and free: nothing here may touch a wallet or its ledger.
    [Fact]
    public async Task RefreshingAndReading_WriteNothingToAnyWalletOrLedger()
    {
        await using (TitanDbContext before = pg.NewDbContext())
        {
            var wallet = Titan.Domain.Aggregates.Billing.Wallet.Wallet.Open(Guid.NewGuid(), "VND");
            before.Wallets.Add(wallet);
            await before.SaveChangesAsync();
            before.WalletTransactions.Add(
                Titan.Domain.Aggregates.Billing.Wallet.WalletTransaction.ForTopUp(wallet.Id, 1000, Guid.NewGuid(), 1000));
            await before.SaveChangesAsync();
        }
        (int wallets, int transactions, int mutations) = await WalletCountsAsync();
        SeedBucket();

        await RefreshAsync();
        await RefreshAsync();
        await ListAsync();

        Assert.True(wallets > 0 && transactions > 0, "the guard must watch at least one wallet and one ledger row");
        Assert.Equal((wallets, transactions, mutations), await WalletCountsAsync());
    }

    private async Task<(int Wallets, int Transactions, int Mutations)> WalletCountsAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await db.Wallets.CountAsync(), await db.WalletTransactions.CountAsync(), await db.WalletMutations.CountAsync());
    }
}
