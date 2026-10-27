using Titan.Domain.Exceptions;
using Titan.Application.Options;
using Titan.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure.Incus;

// The catalog reads a zone's Incus cluster live (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-incus). Bodies are
// the lab recordings, which is where the trap comes from: an untrusted client gets 200 with an empty image list — a
// catalog that looks healthy and is not.
public class IncusCatalogProviderTests
{
    // The clusters start empty: each test configures the ones it needs.
    private static async Task<(IncusCatalogProvider Catalog, ZoneOptions Clusters, string TitanFingerprint)> Catalog()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        var clusters = new ZoneOptions();
        var catalog = new IncusCatalogProvider(new IncusClientFactory(store), clusters,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<IncusCatalogProvider>.Instance);
        return (catalog, clusters, titan.Fingerprint);
    }

    [Fact]
    public async Task AClusterThatDoesNotTrustTitan_IsAnError_NotAnEmptyCatalog()
    {
        var (catalog, configured, _) = await Catalog();
        await using var server = await FakeIncusServer.StartAsync();
        configured.Zones.Add(IncusTestKit.Cluster("mock", server.Endpoint, server.Fingerprint));

        var images = await Assert.ThrowsAsync<ServiceUnavailableException>(() => catalog.ListOsImagesAsync());

        Assert.Equal("CLUSTER_UNTRUSTED", images.Code);
    }

    [Fact]
    public async Task AServerThatIsNotThePinnedOne_IsAnError()
    {
        var (catalog, configured, titan) = await Catalog();
        await using var server = await FakeIncusServer.StartAsync(titan);
        configured.Zones.Add(IncusTestKit.Cluster("mock", server.Endpoint, new string('a', 64)));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => catalog.ListOsImagesAsync());

        Assert.Equal("CLUSTER_FINGERPRINT_MISMATCH", error.Code);
    }

    [Fact]
    public async Task AnUnconfiguredCluster_IsNotFound()
    {
        var (catalog, _, _) = await Catalog();

        var error = await Assert.ThrowsAsync<NotFoundException>(() => catalog.ListOsImagesAsync());

        Assert.Equal("CLUSTER_NOT_FOUND", error.Code);
    }

    // The lab holds one VM image and one container image of Ubuntu noble; Titan only sells VMs.
    [Fact]
    public async Task OsImages_AreTheVirtualMachineImages_OfTheFirstZone()
    {
        var (catalog, configured, titan) = await Catalog();
        await using var server = await FakeIncusServer.StartAsync(titan);
        configured.Zones.Add(IncusTestKit.Cluster("singapore01", server.Endpoint, server.Fingerprint));
        configured.Zones.Add(IncusTestKit.Cluster("mock", "https://unreachable.invalid:8443", IncusTestKit.AnyFingerprint));

        var images = await catalog.ListOsImagesAsync();

        var only = Assert.Single(images);
        Assert.Equal("b89557e2fde36cf2492fcca65f8df6975a5a0e220cd386e2f86d69940db0bb3b", only.Id);
        Assert.Equal("Ubuntu noble arm64 (20261006_07:42)", only.Name);
        Assert.Equal(1, only.SizeGb); // 353,603,748 bytes, rounded up: an image never fits in less than itself
    }

    // The catalog routes are anonymous: the error names what is wrong, never the internal endpoint or fingerprints.
    [Fact]
    public async Task AnAnonymousCaller_SeesNoInternalEndpointOrFingerprint()
    {
        var (catalog, configured, titan) = await Catalog();
        await using var server = await FakeIncusServer.StartAsync();
        configured.Zones.Add(IncusTestKit.Cluster("mock", server.Endpoint, server.Fingerprint));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => catalog.ListOsImagesAsync());

        Assert.Equal("CLUSTER_UNTRUSTED", error.Code);
        Assert.DoesNotContain(server.Endpoint, error.Message);
        Assert.DoesNotContain(server.Fingerprint, error.Message);
        Assert.DoesNotContain(titan, error.Message);
    }

    [Fact]
    public async Task AnAnswerThatIsNotIncusShaped_IsAClusterError()
    {
        var (catalog, configured, titan) = await Catalog();
        await using var server = await FakeIncusServer.StartAsync(titan);
        server.TrustedOverrides["/1.0/images?recursion=1"] = (200, """{"type":"sync","metadata":{"images":"yes"}}""");
        configured.Zones.Add(IncusTestKit.Cluster("mock", server.Endpoint, server.Fingerprint));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => catalog.ListOsImagesAsync());

        Assert.Equal("CLUSTER_ERROR", error.Code);
    }
}
