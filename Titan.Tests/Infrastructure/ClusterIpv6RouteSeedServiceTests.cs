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

    // One pass when the PostSync hook Job calls POST /internal/jobs/ipv6-route-seed after every sync, so a cluster added
    // to the config gets its routes without a pod restart (titan-ops features/network/design.md#ipv6-public).
    [Fact]
    public async Task RunAsync_IsOnePass_UnderItsJobName()
    {
        var routes = new Mock<IClusterIpv6RouteRepository>();
        var job = new ClusterIpv6RouteSeedService(routes.Object, Clusters(("both", "ovnup", "2001:db8::/56")));

        await job.RunAsync(CancellationToken.None);

        Assert.Equal("ipv6-route-seed", job.Name);
        routes.Verify(r => r.EnsureRoutesAsync("both", It.IsAny<CancellationToken>()), Times.Once);
    }
}
