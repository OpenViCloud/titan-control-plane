using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
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
    public void RunsEveryHour_ByDefault()
    {
        Assert.Equal(60, new UsageRecordRetentionOptions().IntervalMinutes);
    }

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

    [Fact]
    public async Task StartsWithoutFaulting_AndStopsOnRequest()
    {
        var service = new UsageRecordRetention(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UsageRecordRetentionOptions()), TimeProvider.System,
            NullLogger<UsageRecordRetention>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully || service.ExecuteTask.IsCanceled);
    }
}
