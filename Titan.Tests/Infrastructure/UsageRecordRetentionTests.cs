using Microsoft.Extensions.Logging.Abstractions;
using Titan.Domain.Aggregates.UsageRecord;
using Titan.Domain.Values;
using Titan.Infrastructure.Hosting;
using Titan.Tests.Services;

namespace Titan.Tests.Infrastructure;

// titan-ops features/billing/metering/design.md#usage-retention: records older than 90 days go, every hour.
public class UsageRecordRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static UsageRecord Record(DateTimeOffset bucket) =>
        UsageRecord.CreateCompute(Guid.NewGuid(), "vm-1", Guid.NewGuid(), bucket, 10, Rate.Of(6000m), "VND");

    [Fact]
    public async Task RunOnce_DeletesTheRecordOneBucketBeforeTheMark_AndKeepsTheOneOnTheMark()
    {
        var repo = new FakeUsageRecordRepository();
        DateTimeOffset mark = UsageRecord.KeptFrom(Now);
        UsageRecord old = Record(mark.AddMinutes(-10));
        UsageRecord onTheMark = Record(mark);
        repo.Seed(old, onTheMark);

        int removed = await UsageRecordRetention.RunOnceAsync(repo, Now, NullLogger.Instance, default);

        Assert.Equal(1, removed);
        Assert.Equal([onTheMark], repo.All);
    }

    [Fact]
    public async Task RunOnce_NothingOlderThanNinetyDays_DeletesNothing()
    {
        var repo = new FakeUsageRecordRepository();
        UsageRecord young = Record(Now.AddDays(-89));
        repo.Seed(young);

        int removed = await UsageRecordRetention.RunOnceAsync(repo, Now, NullLogger.Instance, default);

        Assert.Equal(0, removed);
        Assert.Equal([young], repo.All);
    }

    // One pass when the CronJob calls POST /internal/jobs/usage-record-retention.
    [Fact]
    public async Task RunAsync_IsOnePass_UnderItsJobName()
    {
        var repo = new FakeUsageRecordRepository();
        repo.Seed(Record(UsageRecord.KeptFrom(DateTimeOffset.UtcNow).AddDays(-1)));
        var job = new UsageRecordRetention(repo, TimeProvider.System, NullLogger<UsageRecordRetention>.Instance);

        await job.RunAsync(CancellationToken.None);

        Assert.Equal("usage-record-retention", job.Name);
        Assert.Empty(repo.All);
    }
}
