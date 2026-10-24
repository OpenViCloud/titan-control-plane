using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

// A bad interval in configuration must not stop the whole host: PeriodicTimer throws on a zero or negative period.
public class BackupStatusRefreshServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(5)]
    public async Task AnyInterval_StartsWithoutFaulting_AndStopsOnRequest(int minutes)
    {
        var service = new BackupStatusRefreshService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new BackupStatusOptions { IntervalMinutes = minutes }),
            NullLogger<BackupStatusRefreshService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully || service.ExecuteTask.IsCanceled);
    }
}
