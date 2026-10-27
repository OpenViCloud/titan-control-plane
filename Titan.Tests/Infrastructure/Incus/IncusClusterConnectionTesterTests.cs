using Microsoft.Extensions.Options;
using Titan.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure.Incus;

// "Test connection" probes an endpoint + fingerprint, from the admin's cluster list. It reports, never throws, and
// tells the three failures apart because each has a different fix: wrong fingerprint (re-copy it), Titan not trusted
// (add Titan's certificate on the cluster), unreachable (network) — https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-incus.
public class IncusClusterConnectionTesterTests
{
    private static async Task<(IncusClusterConnectionTester Tester, string TitanFingerprint)> Tester()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        return (new IncusClusterConnectionTester(new IncusClientFactory(store)), titan.Fingerprint);
    }

    // The endpoint is 127.0.0.1 and the server certificate names only "incus-lab": the pin is what lets it through.
    [Fact]
    public async Task TrustedAndPinned_IsOk()
    {
        var (tester, titanFingerprint) = await Tester();
        await using var server = await FakeIncusServer.StartAsync(titanFingerprint);

        var result = await tester.TestAsync(server.Endpoint, server.Fingerprint, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Null(result.Error);
        Assert.NotNull(result.LatencyMs);
    }

    [Fact]
    public async Task APinThatIsNotThisServer_FailsOnTheFingerprint_AndReadsNothing()
    {
        var (tester, titanFingerprint) = await Tester();
        await using var server = await FakeIncusServer.StartAsync(titanFingerprint);
        string otherFingerprint = new string('a', 64);

        var result = await tester.TestAsync(server.Endpoint, otherFingerprint, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("fingerprint", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(server.Fingerprint, result.Error);
    }

    [Fact]
    public async Task ClusterThatDoesNotTrustTitan_FailsWithTheCertificateToTrust()
    {
        var (tester, titanFingerprint) = await Tester();
        await using var server = await FakeIncusServer.StartAsync();

        var result = await tester.TestAsync(server.Endpoint, server.Fingerprint, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("trust", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(titanFingerprint, result.Error);
    }

    [Fact]
    public async Task NothingListening_FailsAsUnreachable()
    {
        var (tester, _) = await Tester();
        string closed;
        await using (var server = await FakeIncusServer.StartAsync())
        {
            closed = server.Endpoint;
        }

        var result = await tester.TestAsync(closed, new string('a', 64), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("reach", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AMalformedFingerprint_IsAResult_NotAnException()
    {
        var (tester, _) = await Tester();

        var result = await tester.TestAsync("https://incus-lab:8443", "nope", CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("fingerprint", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    // A 200 that is not the Incus shape (a proxy page, another service on the port) is a result, not a 500.
    [Fact]
    public async Task AnAnswerThatIsNotIncusShaped_IsAResult_NotAnException()
    {
        var (tester, titanFingerprint) = await Tester();
        await using var server = await FakeIncusServer.StartAsync(titanFingerprint);
        server.TrustedOverrides["/1.0"] = (200, """{"type":"sync","metadata":null}""");

        var result = await tester.TestAsync(server.Endpoint, server.Fingerprint, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Incus", result.Error);
    }

    // No identity configured (allowed only while no cluster is): the admin is told which setting, not given a 500.
    [Fact]
    public async Task NoClientIdentity_IsAResultNamingTheSetting()
    {
        var noIdentity = new ProviderClientIdentityStore(Options.Create(new ProviderClientIdentityOptions()));
        var tester = new IncusClusterConnectionTester(new IncusClientFactory(noIdentity));

        var result = await tester.TestAsync("https://incus-lab:8443", new string('a', 64), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("ProviderClientIdentity", result.Error);
    }
}
