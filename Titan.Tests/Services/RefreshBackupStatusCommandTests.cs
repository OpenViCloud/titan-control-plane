using System.Text;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Commands.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Aggregates.Platform.BackupRecord;
using Titan.Application.Options;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// The refresh turns what the bucket shows into the cache rows. The bucket is the truth: whatever it no longer shows is gone,
// and a bucket that cannot be read changes nothing but the observation.
public class RefreshBackupStatusCommandTests
{
    // The layout barman writes, spelled out here rather than taken from the code under test.
    private const string Base = "postgres/titan-db/base/";
    private const string Wal = "postgres/titan-db/wals/";
    private const string RestoredBase = "postgres/titan-db-restored/base/";
    private const string RestoredWal = "postgres/titan-db-restored/wals/";
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryBackupBucket _bucket = new();
    private readonly FakeBackupRecordRepository _records = new();
    private readonly FakeBackupObservationRepository _observations = new();
    private readonly BackupTargetOptions _target = ConfiguredBackupTarget.Valid();
    private readonly FixedBackupBucketFactory _factory;

    public RefreshBackupStatusCommandTests()
    {
        _factory = new FixedBackupBucketFactory(_bucket);
    }

    private Task RunAsync() => new RefreshBackupStatusCommandHandler(
        ConfiguredBackupTarget.Options(_target), _factory, _records, _observations, new FixedClock(Now))
        .HandleAsync(new RefreshBackupStatusCommand());

    private void Put(string key, string content, DateTimeOffset modified, long? size = null)
    {
        _bucket.Objects[key] = size is null ? Encoding.UTF8.GetBytes(content) : new byte[size.Value];
        _bucket.Modified[key] = modified;
    }

    private void FinishedBase(string id, string endTime, long dataSize = 1000, DateTimeOffset? infoModified = null)
    {
        Put(Base + id + "/backup.info", $"status=DONE\nend_time={endTime}\n", infoModified ?? Now.AddHours(-1));
        Put(Base + id + "/data.tar", "", Now.AddHours(-1), dataSize);
    }

    private BackupObservation Observation => Assert.Single(_observations.Rows, o => o.Kind == BackupRecord.PostgresKind);

    [Fact]
    public async Task AFinishedBase_BecomesACompleteRow_WithBarmansEndTime_AndTheFolderSize()
    {
        FinishedBase("20261024T140631", "2026-10-24 14:06:33+00:00", dataSize: 1000);

        await RunAsync();

        var row = Assert.Single(_records.Rows);
        Assert.Equal("20261024T140631", row.BackupKey);
        Assert.Equal(BackupRecordStatus.Complete, row.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 14, 6, 33, TimeSpan.Zero), row.TakenAt);
        Assert.Equal(1000 + "status=DONE\nend_time=2026-10-24 14:06:33+00:00\n".Length, row.SizeBytes);
        Assert.Equal(BackupReadOutcome.Read, Observation.Outcome);
    }

    [Fact]
    public async Task TheNewestLogObject_IsTheLastWal_AndLogsAreNotBackups()
    {
        FinishedBase("20261024T140631", "2026-10-24 14:06:33+00:00");
        Put(Wal + "0000000100000000/000000010000000000000A0.gz", "w", Now.AddHours(-3));
        Put(Wal + "0000000100000000/000000010000000000000A1.gz", "w", Now.AddMinutes(-4));
        Put(Wal + "0000000100000000/000000010000000000000A2.gz", "w", Now.AddHours(-1));
        // A base backup written after the newest log must not be mistaken for a log.
        Put(Base + "20261025T115900/data.tar", "", Now.AddMinutes(-1), 10);

        await RunAsync();

        Assert.Equal(Now.AddMinutes(-4), Observation.LastWalAt);
        Assert.Equal(new[] { "20261024T140631", "20261025T115900" }, _records.Rows.Select(r => r.BackupKey).OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task AMarkerObjectOrAnOverlongFolderName_IsSkipped_NotAFailedRefresh()
    {
        FinishedBase("K1", "2026-10-24 14:06:33+00:00");
        Put(Base, "", Now.AddHours(-5));
        Put(Base + new string('x', 129) + "/data.tar", "", Now.AddHours(-5), 10);

        await RunAsync();

        Assert.Equal("K1", Assert.Single(_records.Rows).BackupKey);
        Assert.Equal(BackupReadOutcome.Read, Observation.Outcome);
    }

    [Fact]
    public async Task ABaseWithoutItsInfoFile_IsIncomplete()
    {
        Put(Base + "20261025T100000/data.tar", "", Now.AddMinutes(-20), 500);

        await RunAsync();

        var row = Assert.Single(_records.Rows);
        Assert.Equal(BackupRecordStatus.Incomplete, row.Status);
        Assert.Equal(500, row.SizeBytes);
    }

    [Theory]
    [InlineData("status=STARTED\n")]
    [InlineData("status=FAILED\nend_time=2026-10-24 14:06:33+00:00\n")]
    [InlineData("")]
    public async Task AnInfoFileThatDoesNotSayDone_IsIncomplete(string info)
    {
        Put(Base + "20261025T100000/backup.info", info, Now.AddMinutes(-20));
        Put(Base + "20261025T100000/data.tar", "", Now.AddMinutes(-20), 500);

        await RunAsync();

        Assert.Equal(BackupRecordStatus.Incomplete, Assert.Single(_records.Rows).Status);
    }

    [Fact]
    public async Task AnEndTimeInTheFuture_FallsBackToTheInfoFilesLastModified_SoItCannotLookFresh()
    {
        Put(Base + "K1/backup.info", "status=DONE\nend_time=2027-01-01 00:00:00+00:00\n", Now.AddMinutes(-20));
        Put(Base + "K1/data.tar", "", Now.AddMinutes(-20), 500);

        await RunAsync();

        Assert.Equal(Now.AddMinutes(-20), Assert.Single(_records.Rows).TakenAt);
    }

    [Fact]
    public async Task ADoneBaseWithoutAnEndTime_FallsBackToItsInfoFilesLastModified()
    {
        Put(Base + "20261025T100000/backup.info", "status=DONE\n", Now.AddMinutes(-20));
        Put(Base + "20261025T100000/data.tar", "", Now.AddMinutes(-20), 500);

        await RunAsync();

        Assert.Equal(Now.AddMinutes(-20), Assert.Single(_records.Rows).TakenAt);
    }

    [Fact]
    public async Task ABaseThatWasUnfinished_BecomesCompleteWhenItFinishes_InTheSameRow()
    {
        Put(Base + "K1/backup.info", "status=STARTED\n", Now.AddMinutes(-20));
        await RunAsync();
        Guid id = Assert.Single(_records.Rows).Id;

        Put(Base + "K1/backup.info", "status=DONE\nend_time=2026-10-25 11:50:00+00:00\n", Now.AddMinutes(-10));
        await RunAsync();

        var row = Assert.Single(_records.Rows);
        Assert.Equal(id, row.Id);
        Assert.Equal(BackupRecordStatus.Complete, row.Status);
    }

    [Fact]
    public async Task ACompleteBase_IsNotReadAgain()
    {
        FinishedBase("K1", "2026-10-24 14:06:33+00:00");
        await RunAsync();
        int reads = 0;
        _bucket.OnGet = (_, stored) => { reads++; return stored; };

        await RunAsync();

        Assert.Equal(0, reads);
        Assert.Equal(BackupRecordStatus.Complete, Assert.Single(_records.Rows).Status);
    }

    [Fact]
    public async Task ABaseTheBucketNoLongerShows_IsRemoved()
    {
        FinishedBase("K1", "2026-10-20 14:00:00+00:00");
        FinishedBase("K2", "2026-10-24 14:00:00+00:00");
        await RunAsync();
        foreach (string key in _bucket.Objects.Keys.Where(k => k.StartsWith(Base + "K1/")).ToList())
        {
            _bucket.Objects.Remove(key);
        }

        await RunAsync();

        Assert.Equal("K2", Assert.Single(_records.Rows).BackupKey);
    }

    [Fact]
    public async Task WithAnEmptiedCache_TheSameRowsComeBack_FromTheBucketAlone()
    {
        FinishedBase("K1", "2026-10-20 14:00:00+00:00", dataSize: 700);
        Put(Base + "K2/data.tar", "", Now.AddMinutes(-9), 300);
        await RunAsync();
        var before = _records.Rows.Select(r => (r.BackupKey, r.Status, r.TakenAt, r.SizeBytes)).OrderBy(r => r.BackupKey).ToList();

        _records.Rows.Clear();
        _observations.Rows.Clear();
        await RunAsync();

        var after = _records.Rows.Select(r => (r.BackupKey, r.Status, r.TakenAt, r.SizeBytes)).OrderBy(r => r.BackupKey).ToList();
        Assert.Equal(before, after);
        Assert.Equal(2, after.Count);
    }

    // After a restore the new cluster archives under another serverName (titan-ops docs/infra/oci/runbook.md#disaster-recovery);
    // the old name gets nothing new. The pair below swaps which name wrote last, so a fixed prefix cannot pass both.
    [Fact]
    public async Task AfterARestore_TheServerThatWroteLast_IsRead_AndTheOldServersBasesAreGone()
    {
        FinishedBase("20261024T140631", "2026-10-24 14:06:33+00:00", infoModified: Now.AddDays(-1));
        Put(Wal + "0000000100000000/000000010000000000000A0.gz", "w", Now.AddHours(-5));
        await RunAsync();
        Put(RestoredWal + "0000000200000000/000000020000000000000A1.gz", "w", Now.AddMinutes(-3));
        Put(RestoredBase + "20261025T113000/backup.info", "status=DONE\nend_time=2026-10-25 11:30:00+00:00\n", Now.AddMinutes(-30));

        await RunAsync();

        var row = Assert.Single(_records.Rows);
        Assert.Equal("20261025T113000", row.BackupKey);
        Assert.Equal(BackupLayout.PostgresResourceName, row.ResourceName);
        Assert.Equal(Now.AddMinutes(-3), Observation.LastWalAt);
    }

    [Fact]
    public async Task WhenTheOriginalNameWroteLast_ItIsTheOneRead()
    {
        Put(RestoredWal + "0000000200000000/000000020000000000000A1.gz", "w", Now.AddHours(-5));
        Put(RestoredBase + "20261020T113000/backup.info", "status=DONE\nend_time=2026-10-20 11:30:00+00:00\n", Now.AddDays(-5));
        FinishedBase("20261024T140631", "2026-10-24 14:06:33+00:00");
        Put(Wal + "0000000100000000/000000010000000000000A0.gz", "w", Now.AddMinutes(-3));

        await RunAsync();

        Assert.Equal("20261024T140631", Assert.Single(_records.Rows).BackupKey);
        Assert.Equal(Now.AddMinutes(-3), Observation.LastWalAt);
    }

    [Fact]
    public async Task ARestoredServerWithLogsButNoBaseYet_HasNoBackupRow_AndItsOwnLastWal()
    {
        FinishedBase("20261024T140631", "2026-10-24 14:06:33+00:00", infoModified: Now.AddDays(-1));
        Put(RestoredWal + "0000000200000000/000000020000000000000A1.gz", "w", Now.AddMinutes(-2));

        await RunAsync();

        Assert.Empty(_records.Rows);
        Assert.Equal(BackupReadOutcome.Read, Observation.Outcome);
        Assert.Equal(Now.AddMinutes(-2), Observation.LastWalAt);
    }

    [Fact]
    public async Task TwoServersThatWroteAtTheSameMoment_TheLargerNameIsRead()
    {
        Put(Wal + "0000000100000000/A0.gz", "w", Now.AddMinutes(-3));
        Put(RestoredWal + "0000000200000000/A1.gz", "w", Now.AddMinutes(-3));
        Put(RestoredBase + "K2/backup.info", "status=DONE\nend_time=2026-10-25 11:30:00+00:00\n", Now.AddHours(-1));
        FinishedBase("K1", "2026-10-24 14:06:33+00:00");

        await RunAsync();

        Assert.Equal("K2", Assert.Single(_records.Rows).BackupKey);
    }

    [Fact]
    public async Task AnObjectDirectlyUnderPostgres_IsNotAServer()
    {
        FinishedBase("20261024T140631", "2026-10-24 14:06:33+00:00");
        Put("postgres/notes.txt", "x", Now.AddMinutes(-1));

        await RunAsync();

        Assert.Equal("20261024T140631", Assert.Single(_records.Rows).BackupKey);
        Assert.Equal(BackupReadOutcome.Read, Observation.Outcome);
    }

    [Fact]
    public async Task OnlyStrayObjectsUnderPostgres_IsNoBackupPrefix()
    {
        Put("postgres/notes.txt", "x", Now.AddMinutes(-1));

        await RunAsync();

        Assert.Equal(BackupReadOutcome.NoBackupPrefix, Observation.Outcome);
        Assert.Empty(_records.Rows);
    }

    [Fact]
    public async Task AnEmptyBucket_IsEmpty_NotUnreadable()
    {
        await RunAsync();

        Assert.Equal(BackupReadOutcome.Empty, Observation.Outcome);
        Assert.Null(Observation.LastWalAt);
        Assert.Empty(_records.Rows);
    }

    [Fact]
    public async Task ABucketWithOtherObjectsOnly_IsNoBackupPrefix_NotEmpty()
    {
        Put("somebody-elses/file.txt", "x", Now.AddDays(-1));

        await RunAsync();

        Assert.Equal(BackupReadOutcome.NoBackupPrefix, Observation.Outcome);
    }

    [Fact]
    public async Task ADeniedBucket_IsUnreadable_KeepsTheRowsAndTheLastWal_AndNamesTheReason()
    {
        FinishedBase("K1", "2026-10-24 14:06:33+00:00");
        Put(Wal + "0000000100000000/A0.gz", "w", Now.AddMinutes(-4));
        await RunAsync();

        _bucket.OnListObjects = _ => throw new BackupBucketException("access denied (AccessDenied)");
        await RunAsync();

        Assert.Single(_records.Rows);
        Assert.Equal(BackupReadOutcome.Unreadable, Observation.Outcome);
        Assert.Equal("access denied (AccessDenied)", Observation.Error);
        Assert.Equal(Now.AddMinutes(-4), Observation.LastWalAt);
    }

    [Fact]
    public async Task AConfigurationThatCannotWork_IsUnreadable_NamingTheSetting_NotACrash()
    {
        _target.Endpoint = "http://acct.r2.cloudflarestorage.com";

        await RunAsync();

        Assert.Equal(BackupReadOutcome.Unreadable, Observation.Outcome);
        Assert.Contains("BackupTarget__Endpoint", Observation.Error);
        Assert.DoesNotContain(ConfiguredBackupTarget.Secret, Observation.Error);
        Assert.Empty(_factory.Opened);
    }

    [Fact]
    public async Task NoKeyPair_ReadsNothing_AndRecordsNothing()
    {
        _target.AccessKeyId = "";
        _target.SecretAccessKey = "";

        await RunAsync();

        Assert.Empty(_factory.Opened);
        Assert.Empty(_observations.Rows);
        Assert.Empty(_records.Rows);
    }

    [Theory]
    [InlineData("", "only-a-secret")]
    [InlineData("only-a-key-id", "")]
    public async Task HalfAKeyPair_IsUnreadable_NamingTheSetting_NotSilence(string keyId, string secret)
    {
        _target.AccessKeyId = keyId;
        _target.SecretAccessKey = secret;

        await RunAsync();

        Assert.Equal(BackupReadOutcome.Unreadable, Observation.Outcome);
        Assert.Contains("BackupTarget__", Observation.Error);
        Assert.Empty(_factory.Opened);
    }

    [Fact]
    public async Task TheBucketIsOpenedWithTheConfiguredTarget_AndClosedAgain()
    {
        await RunAsync();

        var opened = Assert.Single(_factory.Opened);
        Assert.Equal(
            new BackupBucketConnection("https://acct.r2.cloudflarestorage.com", "auto", "titan-backup-dev", "key-id", ConfiguredBackupTarget.Secret), opened);
        Assert.True(_bucket.Disposed);
    }
}
