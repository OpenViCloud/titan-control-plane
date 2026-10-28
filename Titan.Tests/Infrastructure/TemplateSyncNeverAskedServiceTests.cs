using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

public class TemplateSyncNeverAskedServiceTests
{
    // A new environment can create VMs a few minutes after its first deploy, not an hour later.
    [Fact]
    public void LooksEveryFiveMinutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), TemplateSyncNeverAskedService.Interval);
    }

    [Fact]
    public async Task StartsWithoutFaulting_AndStopsOnRequest()
    {
        var service = new TemplateSyncNeverAskedService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<TemplateSyncNeverAskedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully || service.ExecuteTask.IsCanceled);
    }
}
