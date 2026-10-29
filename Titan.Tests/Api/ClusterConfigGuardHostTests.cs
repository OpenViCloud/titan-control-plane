using Titan.Tests.Infrastructure.Incus;

namespace Titan.Tests.Api;

// FullHost — cluster and identity are checked at startup, so only a real host start shows the checks are wired in
// (titan-ops features/cluster/design.md#cluster-from-config). The control case starts the same host with a
// valid configuration, so a failure of the others can only come from the value each one spoils.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ClusterConfigGuardHostTests
{
    private const string Fingerprint = "ffcf7803b2371e90834c2dd2a454f48bcb9c541b16dd7dc54a93a104b1aa108f";

    private static Dictionary<string, string> AValidCluster(string endpoint = "https://localhost:8443")
    {
        var identity = IncusTestKit.NewIdentity();
        return new Dictionary<string, string>
        {
            ["environment"] = "Staging",
            ["Clusters:0:Id"] = "singapore01",
            ["Clusters:0:Name"] = "Singapore 01",
            ["Clusters:0:Endpoint"] = endpoint,
            ["Clusters:0:ServerFingerprint"] = Fingerprint,
            ["ProviderClientIdentity:CertificatePem"] = identity.CertificatePem,
            ["ProviderClientIdentity:KeyPem"] = identity.KeyPem,
        };
    }

    [Fact]
    public async Task AValidClusterAndIdentity_Starts()
    {
        await using var factory = new StagingFactory(AValidCluster());
        await factory.InitializeAsync();

        var client = factory.CreateClient();
        var response = await client.GetAsync("/healthz");

        Assert.NotEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AClusterOverPlainHttp_DoesNotStart()
    {
        await using var factory = new StagingFactory(AValidCluster(endpoint: "http://localhost:8443"));
        await factory.InitializeAsync();

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Clusters:0:Endpoint", ex.ToString());
    }

    [Fact]
    public async Task AClusterWithoutTitansIdentity_DoesNotStart()
    {
        var settings = AValidCluster();
        settings.Remove("ProviderClientIdentity:CertificatePem");
        settings.Remove("ProviderClientIdentity:KeyPem");
        await using var factory = new StagingFactory(settings);
        await factory.InitializeAsync();

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("ProviderClientIdentity", ex.ToString());
    }

    private sealed class StagingFactory(Dictionary<string, string> settings) : RealHostFactory(
        new Dictionary<string, string?> { ["Keycloak:Authority"] = "http://keycloak.test/realms/titan" },
        settings)
    {
    }
}
