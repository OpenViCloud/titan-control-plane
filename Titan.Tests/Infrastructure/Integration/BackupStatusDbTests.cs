using System.Net;
using System.Net.Sockets;
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

namespace Titan.Tests.Infrastructure.Integration;

/// The backup cache on Postgres through the real handlers, repositories, S3 adapter and clock: rows written and read back,
/// the unique index, a bucket that cannot be reached (a real connection refused: the saved target points at a port nobody
/// listens on) leaving the rows and the last log time in place, and no wallet or ledger row touched by any of it
/// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/backup/user-stories.md#bak-03). A bucket that answers is
/// not reproduced here: R2 cannot run in CI and no service is faked, so what a listing turns into rows is unit-tested
/// (RefreshBackupStatusCommandTests).
/// Category=Integration — its own CI step; needs real Postgres.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class BackupStatusDbTests(PostgresFixture pg) : IAsyncLifetime
{
    private static ClusterKeyBackupSecretProtector Protector() =>
        new(new AesGcmClusterCredentialCipher(Options.Create(new ClusterEncryptionOptions
        {
            ClusterKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        })));

    private static int PortNobodyListensOn()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // To the second: the column keeps microseconds, so a time with more digits would not come back equal.
    private static DateTimeOffset Ago(TimeSpan span)
    {
        DateTimeOffset at = DateTimeOffset.UtcNow - span;
        return new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, TimeSpan.Zero);
    }

    public async Task InitializeAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        await db.Backups.ExecuteDeleteAsync();
        await db.BackupObservations.ExecuteDeleteAsync();
        await db.BackupTargets.ExecuteDeleteAsync();

        await using TitanDbContext setDb = pg.NewDbContext();
        await new SetBackupTargetCommandHandler(
            new BackupTargetRepository(setDb), Protector(), new AdminAuditLogRepository(setDb), Callers.Admin("kc-backup-status"))
            .HandleAsync(new SetBackupTargetCommand($"https://127.0.0.1:{PortNobodyListensOn()}", "auto", "titan-backup-dev", "key-id", "S3CR3T-value"));
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedAsync(DateTimeOffset baseAt, DateTimeOffset lastWalAt)
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "K1", Ago(TimeSpan.FromDays(3)), 100, BackupRecordStatus.Complete, DateTimeOffset.UtcNow));
        db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "K2", baseAt, 5000, BackupRecordStatus.Complete, DateTimeOffset.UtcNow));
        db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "K3", Ago(TimeSpan.FromMinutes(5)), 700, BackupRecordStatus.Incomplete, DateTimeOffset.UtcNow));
        var observation = BackupObservation.Create("postgres", "titan-db");
        observation.RecordRead(BackupReadOutcome.Read, lastWalAt, DateTimeOffset.UtcNow);
        db.BackupObservations.Add(observation);
        await db.SaveChangesAsync();
    }

    private async Task RefreshAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        await new RefreshBackupStatusCommandHandler(
            new BackupTargetRepository(db), Protector(), new S3BackupBucketFactory(),
            new BackupRecordRepository(db), new BackupObservationRepository(db), TimeProvider.System)
            .HandleAsync(new RefreshBackupStatusCommand());
    }

    private async Task<BackupsQueryResult> ListAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await new ListBackupsQueryHandler(new BackupRecordRepository(db), new BackupObservationRepository(db), TimeProvider.System)
            .HandleAsync(new ListBackupsQuery());
    }

    [Fact]
    public async Task SeededRows_ComeBackThroughThePage_NewestCompleteBaseAndLastLog()
    {
        DateTimeOffset baseAt = Ago(TimeSpan.FromHours(2));
        DateTimeOffset walAt = Ago(TimeSpan.FromMinutes(4));
        await SeedAsync(baseAt, walAt);

        var item = Assert.Single((await ListAsync()).Items);

        Assert.Equal(BackupHealth.Ok, item.Health);
        Assert.Equal(baseAt, item.TakenAt);
        Assert.Equal(5000, item.SizeBytes);
        Assert.Equal(walAt, item.LastWalAt);
    }

    [Fact]
    public async Task AFreshBaseAndAnOldLog_IsOk_ButAnOldBaseIsStale()
    {
        await SeedAsync(Ago(TimeSpan.FromHours(3)), Ago(TimeSpan.FromDays(2)));
        Assert.Equal(BackupHealth.Ok, Assert.Single((await ListAsync()).Items).Health);

        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Backups.ExecuteDeleteAsync();
            db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "OLD", Ago(TimeSpan.FromHours(30)), 1, BackupRecordStatus.Complete, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var item = Assert.Single((await ListAsync()).Items);
        Assert.Equal((BackupHealth.Stale, BackupHealthPolicy.BaseTooOld), (item.Health, item.Reason));
    }

    [Fact]
    public async Task TwoRowsForTheSameBackup_AreRefusedByTheDatabase()
    {
        await using TitanDbContext db = pg.NewDbContext();
        db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "K1", DateTimeOffset.UtcNow, 1, BackupRecordStatus.Complete, DateTimeOffset.UtcNow));
        db.Backups.Add(BackupRecord.Create("postgres", "titan-db", "K1", DateTimeOffset.UtcNow, 1, BackupRecordStatus.Complete, DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task ABucketThatCannotBeReached_KeepsTheRowsAndTheLastLog_AndThePageSaysUnknown()
    {
        DateTimeOffset walAt = Ago(TimeSpan.FromMinutes(4));
        await SeedAsync(Ago(TimeSpan.FromHours(2)), walAt);

        await RefreshAsync();

        await using TitanDbContext db = pg.NewDbContext();
        Assert.Equal(3, await db.Backups.CountAsync());
        var observation = await db.BackupObservations.AsNoTracking().SingleAsync();
        Assert.Equal(BackupReadOutcome.Unreadable, observation.Outcome);
        Assert.Equal("could not reach the endpoint", observation.Error);
        Assert.Equal(walAt, observation.LastWalAt);
        var item = Assert.Single((await ListAsync()).Items);
        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.BucketUnreadable), (item.Health, item.Reason));
    }

    [Fact]
    public async Task WithNothingReadYet_ThePageIsUnknown_NotMissing()
    {
        var item = Assert.Single((await ListAsync()).Items);

        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.NotReadYet), (item.Health, item.Reason));
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
        await SeedAsync(Ago(TimeSpan.FromHours(2)), Ago(TimeSpan.FromMinutes(4)));

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
