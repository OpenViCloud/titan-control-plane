using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.Platform.BackupObservation;
using Titan.Domain.Aggregates.Platform.BackupRecord;

namespace Titan.Tests.Services;

// What the page says about Postgres. The cases that matter most: "could not read" is never "no backup", a bucket that has
// other objects but no Postgres prefix says so, and an idle database (old log, fresh base) is healthy.
public class BackupHealthPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Limit = BackupHealthPolicy.PostgresBaseStaleAfter;

    private static BackupObservation Read(BackupReadOutcome outcome, TimeSpan? readAgo = null, TimeSpan? walAgo = null)
    {
        var observation = BackupObservation.Create(BackupRecord.PostgresKind, "titan-db");
        observation.RecordRead(outcome, walAgo is null ? null : Now - walAgo, Now - (readAgo ?? TimeSpan.FromMinutes(1)));
        return observation;
    }

    private static BackupRecord Base(TimeSpan age, BackupRecordStatus status = BackupRecordStatus.Complete) =>
        BackupRecord.Create(BackupRecord.PostgresKind, "titan-db", $"k{age.Ticks}-{status}", Now - age, 100, status, Now);

    [Fact]
    public void NoReadingYet_IsUnknown()
    {
        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.NotReadYet), BackupHealthPolicy.ForPostgres(null, new List<BackupRecord>(), Now));
    }

    [Fact]
    public void AnUnreadableBucket_IsUnknown_NotMissing_EvenWithRowsKnownFromBefore()
    {
        var observation = Read(BackupReadOutcome.Read);
        observation.RecordUnreadable("access denied (AccessDenied)", Now);

        var result = BackupHealthPolicy.ForPostgres(observation, new[] { Base(TimeSpan.FromHours(1)) }, Now);

        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.BucketUnreadable), result);
    }

    [Fact]
    public void AReadingOlderThanItsLimit_IsUnknown_BecauseTheRefreshItselfStopped()
    {
        var observation = Read(BackupReadOutcome.Read, readAgo: BackupHealthPolicy.ReadingMaxAge + TimeSpan.FromSeconds(1));

        var result = BackupHealthPolicy.ForPostgres(observation, new[] { Base(TimeSpan.FromHours(1)) }, Now);

        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.StatusTooOld), result);
    }

    [Fact]
    public void AReadingJustInsideItsLimit_IsStillTrusted()
    {
        var observation = Read(BackupReadOutcome.Read, readAgo: BackupHealthPolicy.ReadingMaxAge);

        Assert.Equal(BackupHealth.Ok, BackupHealthPolicy.ForPostgres(observation, new[] { Base(TimeSpan.FromHours(1)) }, Now).Health);
    }

    [Fact]
    public void AnEmptyBucket_IsMissing_NoBackup()
    {
        Assert.Equal((BackupHealth.Missing, BackupHealthPolicy.NoBackup),
            BackupHealthPolicy.ForPostgres(Read(BackupReadOutcome.Empty), new List<BackupRecord>(), Now));
    }

    [Fact]
    public void ABucketWithOtherObjectsButNoPostgresPrefix_IsMissing_WithItsOwnReason()
    {
        Assert.Equal((BackupHealth.Missing, BackupHealthPolicy.NoPostgresBackupInBucket),
            BackupHealthPolicy.ForPostgres(Read(BackupReadOutcome.NoBackupPrefix), new List<BackupRecord>(), Now));
    }

    [Fact]
    public void ReadWithoutAnyBase_IsMissing()
    {
        Assert.Equal((BackupHealth.Missing, BackupHealthPolicy.NoBackup),
            BackupHealthPolicy.ForPostgres(Read(BackupReadOutcome.Read), new List<BackupRecord>(), Now));
    }

    [Fact]
    public void OnlyUnfinishedBases_IsMissing_SayingSo()
    {
        var result = BackupHealthPolicy.ForPostgres(
            Read(BackupReadOutcome.Read), new[] { Base(TimeSpan.FromMinutes(5), BackupRecordStatus.Incomplete) }, Now);

        Assert.Equal((BackupHealth.Missing, BackupHealthPolicy.OnlyIncompleteBackups), result);
    }

    [Fact]
    public void ABaseAtTheLimit_IsOk_AndOneSecondOlder_IsStale()
    {
        var observation = Read(BackupReadOutcome.Read);

        Assert.Equal(BackupHealth.Ok, BackupHealthPolicy.ForPostgres(observation, new[] { Base(Limit) }, Now).Health);
        Assert.Equal((BackupHealth.Stale, BackupHealthPolicy.BaseTooOld),
            BackupHealthPolicy.ForPostgres(observation, new[] { Base(Limit + TimeSpan.FromSeconds(1)) }, Now));
    }

    [Fact]
    public void TheNewestCompleteBase_Decides_NotAnOldOneNorAnUnfinishedNewerOne()
    {
        var observation = Read(BackupReadOutcome.Read);
        var rows = new[]
        {
            Base(TimeSpan.FromDays(3)),
            Base(TimeSpan.FromHours(2)),
            Base(TimeSpan.FromMinutes(1), BackupRecordStatus.Incomplete),
        };

        Assert.Equal(BackupHealth.Ok, BackupHealthPolicy.ForPostgres(observation, rows, Now).Health);
    }

    [Fact]
    public void AnIdleDatabase_OldLogFreshBase_IsOk()
    {
        var observation = Read(BackupReadOutcome.Read, walAgo: TimeSpan.FromDays(2));

        Assert.Equal((BackupHealth.Ok, (string?)null), BackupHealthPolicy.ForPostgres(observation, new[] { Base(TimeSpan.FromHours(3)) }, Now));
    }
}
