using Titan.Application.Options;
using Titan.Infrastructure.Incus;

namespace Titan.Tests.Services;

// The admin list is the configuration, nothing else (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-from-config):
// every configured cluster shows with its connection and provider settings, in config order, and nothing secret.
public class AdminClusterServiceTests
{
    private const string Fingerprint = "ffcf7803b2371e90834c2dd2a454f48bcb9c541b16dd7dc54a93a104b1aa108f";

    [Fact]
    public async Task List_ShowsEveryConfiguredCluster_InConfigOrder()
    {
        var zones = new ZoneOptions
        {
            Zones =
            [
                new ZoneConfig { Id = "singapore01", Name = "Singapore 01", Endpoint = "https://localhost:8443", ServerFingerprint = Fingerprint },
                new ZoneConfig { Id = "local-mac", Name = "Local Mac", Endpoint = "https://titan-incus:8443", ServerFingerprint = Fingerprint },
            ],
        };

        var list = await new AdminClusterService(zones).ListForAdminAsync(CancellationToken.None);

        Assert.Equal(["singapore01", "local-mac"], list.Select(c => c.ZoneId).ToList());
        var local = list[1];
        Assert.Equal("Local Mac", local.Name);
        Assert.Equal("https://titan-incus:8443", local.Endpoint);
        Assert.Equal(Fingerprint, local.ServerFingerprint);
    }

    [Fact]
    public async Task List_CarriesTheProviderSettings()
    {
        var zones = new ZoneOptions
        {
            Zones =
            [
                new ZoneConfig
                {
                    Id = "singapore01", Name = "Singapore 01", Endpoint = "https://localhost:8443", ServerFingerprint = Fingerprint,
                    OvnUplinkNetwork = "ovnup", StoragePool = "default", Ipv6Block = "2001:41d0:d00:d400::/56",
                },
            ],
        };

        var cluster = Assert.Single(await new AdminClusterService(zones).ListForAdminAsync(CancellationToken.None));

        Assert.Equal("ovnup", cluster.ProviderSettings.OvnUplinkNetwork);
        Assert.Equal("default", cluster.ProviderSettings.StoragePool);
        Assert.Equal("2001:41d0:d00:d400::/56", cluster.ProviderSettings.Ipv6Block);
    }

    [Fact]
    public async Task List_NoClusterConfigured_IsEmpty()
    {
        var list = await new AdminClusterService(new ZoneOptions()).ListForAdminAsync(CancellationToken.None);

        Assert.Empty(list);
    }
}
