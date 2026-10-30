using Titan.Application.Commands.Platform.Backup;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;
using Titan.Domain.Aggregates.BackupObservation;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class ListBackupsQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeBackupRecordRepository _records = new();
    private readonly FakeBackupObservationRepository _observations = new();

    private Task<BackupsQueryResult> RunAsync() =>
        new ListBackupsQueryHandler(_records, _observations, new FixedClock(Now)).HandleAsync(new ListBackupsQuery());

    private void Row(string key, TimeSpan age, long size, BackupRecordStatus status = BackupRecordStatus.Complete) =>
        _records.Rows.Add(BackupRecord.Create(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName, key, Now - age, size, status, Now));

    [Fact]
    public async Task NothingReadYet_IsOneUnknownItem_WithNoReadTime()
    {
        var result = await RunAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.NotReadYet), (item.Health, item.Reason));
        Assert.Null(result.ReadAt);
        Assert.Null(item.TakenAt);
    }

    [Fact]
    public async Task TheNewestCompleteBase_IsReturned_NotTheOldestAndNotAnUnfinishedOne()
    {
        var observation = BackupObservation.Create(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName);
        observation.RecordRead(BackupReadOutcome.Read, Now.AddMinutes(-3), Now.AddMinutes(-1));
        _observations.Rows.Add(observation);
        Row("old", TimeSpan.FromDays(3), 100);
        Row("new", TimeSpan.FromHours(2), 300);
        Row("unfinished", TimeSpan.FromMinutes(5), 50, BackupRecordStatus.Incomplete);

        var result = await RunAsync();

        var item = Assert.Single(result.Items);
        Assert.Equal(BackupRecord.PostgresKind, item.Kind);
        Assert.Equal("titan-db", item.Name);
        Assert.Null(item.TenantId);
        Assert.Equal(Now.AddHours(-2), item.TakenAt);
        Assert.Equal(300, item.SizeBytes);
        Assert.Equal(Now.AddMinutes(-3), item.LastWalAt);
        Assert.Equal(BackupHealth.Ok, item.Health);
        Assert.Equal(Now.AddMinutes(-1), result.ReadAt);
    }

    [Fact]
    public async Task AnUnreadableBucket_ShowsTheRowsButSaysUnknown()
    {
        var observation = BackupObservation.Create(BackupRecord.PostgresKind, BackupLayout.PostgresResourceName);
        observation.RecordUnreadable("could not reach the endpoint", Now.AddMinutes(-1));
        _observations.Rows.Add(observation);
        Row("k", TimeSpan.FromHours(2), 300);

        var item = Assert.Single((await RunAsync()).Items);

        Assert.Equal((BackupHealth.Unknown, BackupHealthPolicy.BucketUnreadable), (item.Health, item.Reason));
        Assert.Equal(300, item.SizeBytes);
    }
}
