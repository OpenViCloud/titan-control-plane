using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

public class BackupScheduleServiceTests
{
    // Hourly: a day's backup that failed is asked again within the hour (the schedule's key holds the day).
    [Fact]
    public void AsksEveryHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), BackupScheduleService.Interval);
    }

    [Fact]
    public async Task StartsWithoutFaulting_AndStopsOnRequest()
    {
        var service = new BackupScheduleService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<BackupScheduleService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully || service.ExecuteTask.IsCanceled);
    }
}
