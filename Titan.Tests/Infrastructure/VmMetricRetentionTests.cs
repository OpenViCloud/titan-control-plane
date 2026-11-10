using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Infrastructure.Hosting;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// titan-ops features/vm/design.md#vm-metrics: samples older than seven days go, every hour.
public class VmMetricRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static VmMetricReading Reading(Guid vmId, DateTimeOffset at) =>
        new(vmId, at, "agent", 1, null, null, null, null, 0, 0);

    [Fact]
    public void KeepsSevenDays_AndRunsEveryHour_ByDefault()
    {
        var options = new VmMetricRetentionOptions();

        Assert.Equal(7, options.RetentionDays);
        Assert.Equal(60, options.IntervalMinutes);
    }

    [Fact]
    public async Task RunOnce_DeletesTheEightDayOldSample_AndKeepsTheOneDayOld()
    {
        var store = new FakeVmMetricStore();
        Guid vmId = Guid.NewGuid();
        VmMetricReading old = Reading(vmId, Now.AddDays(-8));
        VmMetricReading young = Reading(vmId, Now.AddDays(-1));
        store.Readings.Add(old);
        store.Readings.Add(young);

        int removed = await VmMetricRetention.RunOnceAsync(store, 7, Now, NullLogger.Instance, default);

        Assert.Equal(1, removed);
        Assert.Equal([young], store.Readings);
    }

    [Fact]
    public async Task StartsWithoutFaulting_AndStopsOnRequest()
    {
        var service = new VmMetricRetention(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new VmMetricRetentionOptions()), TimeProvider.System,
            NullLogger<VmMetricRetention>.Instance);

        await service.StartAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask!.IsFaulted);
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask.IsCompletedSuccessfully || service.ExecuteTask.IsCanceled);
    }
}
