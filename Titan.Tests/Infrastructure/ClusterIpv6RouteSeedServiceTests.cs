using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Titan.Application.Options;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

public class ClusterIpv6RouteSeedServiceTests
{
    private static ClusterOptions Clusters(params (string Id, string? Uplink, string? Block)[] clusters) => new()
    {
        Clusters = clusters.Select(c => new ClusterConfig
        {
            Id = c.Id, Name = c.Id, Endpoint = "https://localhost:8443", ServerFingerprint = new string('a', 64),
            OvnUplinkNetwork = c.Uplink, Ipv6Block = c.Block,
        }).ToList(),
    };

    [Fact]
    public async Task RunOnce_MakesRoutes_OnlyForAClusterWithBothUplinkAndBlock()
    {
        var routes = new Mock<IClusterIpv6RouteRepository>();

        await ClusterIpv6RouteSeedService.RunOnceAsync(routes.Object,
            Clusters(("both", "ovnup", "2001:db8::/56"), ("no-block", "ovnup", null), ("no-uplink", null, "2001:db8::/56"), ("neither", null, null)),
            CancellationToken.None);

        routes.Verify(r => r.EnsureRoutesAsync("both", It.IsAny<CancellationToken>()), Times.Once);
        routes.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task AFailure_LeavesTheServiceRunning_WaitingToTryAgain()
    {
        var routes = new Mock<IClusterIpv6RouteRepository>();
        routes.Setup(r => r.EnsureRoutesAsync("both", It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("no database"));
        var services = new ServiceCollection();
        services.AddSingleton(routes.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var service = new ClusterIpv6RouteSeedService(provider.GetRequiredService<IServiceScopeFactory>(),
            Clusters(("both", "ovnup", "2001:db8::/56")), NullLogger<ClusterIpv6RouteSeedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.False(service.ExecuteTask!.IsCompleted);
        routes.Verify(r => r.EnsureRoutesAsync("both", It.IsAny<CancellationToken>()), Times.Once);
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ASuccess_EndsTheService()
    {
        var routes = new Mock<IClusterIpv6RouteRepository>();
        var services = new ServiceCollection();
        services.AddSingleton(routes.Object);
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var service = new ClusterIpv6RouteSeedService(provider.GetRequiredService<IServiceScopeFactory>(),
            Clusters(("both", "ovnup", "2001:db8::/56")), NullLogger<ClusterIpv6RouteSeedService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));

        routes.Verify(r => r.EnsureRoutesAsync("both", It.IsAny<CancellationToken>()), Times.Once);
    }
}
