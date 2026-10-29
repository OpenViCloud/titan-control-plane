using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Titan.Application.Services.Billing.Metering;
using Titan.Domain.Repositories.Billing;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Repositories.Compute;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/money-and-wallet.md#append-only-metering — UsageResourceType / meter Name is a closed set
// the compiler does not check for exhaustiveness, so the money-path hazard is silently dropping a meter.
// AddMeters (the path Program.cs calls) must expose exactly {Compute, Disk, Snapshot} as IMeter.
public class MeterRegistrationTests
{
    [Fact]
    public void AddMeters_registers_exactly_the_three_named_meters()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();

        // The meter services' own dependencies — stubbed; the meters are never ticked here, only
        // resolved so their Name is read.
        services.AddScoped(_ => Mock.Of<IVmRepository>());
        services.AddScoped(_ => Mock.Of<IUsageRecordRepository>());
        services.AddScoped(_ => Mock.Of<IVmOfferingRepository>());
        services.AddScoped(_ => Mock.Of<IVolumeOfferingRepository>());
        services.AddScoped(_ => Mock.Of<ISnapshotOfferingRepository>());
        services.AddScoped(_ => Mock.Of<IVolumeRepository>());
        services.AddScoped(_ => Mock.Of<IVmSnapshotRepository>());
        services.AddScoped(_ => Mock.Of<IVolumeSnapshotRepository>());

        services.AddMeters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var names = scope.ServiceProvider.GetServices<IMeter>()
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Compute", "Disk", "Snapshot" }, names);
    }
}
