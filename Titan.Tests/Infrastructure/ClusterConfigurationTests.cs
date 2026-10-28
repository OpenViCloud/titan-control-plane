using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Titan.Application.Options;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Infrastructure;

// A cluster is config, checked once at startup: a value the handshake would refuse stops the control plane instead
// of failing the first customer request (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-from-config).
public class ClusterConfigurationTests
{
    private const string Fingerprint = "ffcf7803b2371e90834c2dd2a454f48bcb9c541b16dd7dc54a93a104b1aa108f";

    private static ClusterConfig Valid(string id = "singapore01") => new()
    {
        Id = id,
        Name = "Singapore 01",
        Endpoint = "https://localhost:8443",
        ServerFingerprint = Fingerprint,
    };

    [Fact]
    public void AValidCluster_IsKept_WithItsEndpointAndFingerprint()
    {
        var options = ClusterOptions.FromConfiguration([Valid()]);

        var cluster = Assert.Single(options.Clusters);
        Assert.Equal("singapore01", cluster.Id);
        Assert.Equal("Singapore 01", cluster.Name);
        Assert.Equal("https://localhost:8443", cluster.Endpoint);
        Assert.Equal(Fingerprint, cluster.ServerFingerprint);
    }

    [Fact]
    public void NoClusterConfigured_IsAnEmptyList()
    {
        var options = ClusterOptions.FromConfiguration(null);

        Assert.Empty(options.Clusters);
    }

    // What `incus remote add` and openssl print is accepted; what the handshake compares is kept.
    [Fact]
    public void TheFingerprint_IsKeptAsLowerCaseHex_WithoutColons()
    {
        var cluster = Valid();
        cluster.ServerFingerprint = string.Join(":", Enumerable.Range(0, 32).Select(i => Fingerprint.Substring(i * 2, 2).ToUpperInvariant()));

        var options = ClusterOptions.FromConfiguration([cluster]);

        Assert.Equal(Fingerprint, options.Clusters[0].ServerFingerprint);
    }

    [Fact]
    public void TheEndpoint_LosesItsTrailingSlash()
    {
        var cluster = Valid();
        cluster.Endpoint = "https://localhost:8443/";

        var options = ClusterOptions.FromConfiguration([cluster]);

        Assert.Equal("https://localhost:8443", options.Clusters[0].Endpoint);
    }

    [Fact]
    public void ABlankName_IsTheId()
    {
        var cluster = Valid();
        cluster.Name = " ";

        var options = ClusterOptions.FromConfiguration([cluster]);

        Assert.Equal("singapore01", options.Clusters[0].Name);
    }

    [Fact]
    public void BlankProviderSettings_AreNotSet()
    {
        var cluster = Valid();
        cluster.OvnUplinkNetwork = " ";
        cluster.StoragePool = "";
        cluster.Ipv6Block = "";

        var options = ClusterOptions.FromConfiguration([cluster]);

        Assert.Null(options.Clusters[0].OvnUplinkNetwork);
        Assert.Null(options.Clusters[0].StoragePool);
        Assert.Null(options.Clusters[0].Ipv6Block);
    }

    [Fact]
    public void TheIpv6Block_IsKeptAsTheFirstAddressOfThe56()
    {
        var cluster = Valid();
        cluster.Ipv6Block = "2001:41D0:0D00:D400::/56";

        var options = ClusterOptions.FromConfiguration([cluster]);

        Assert.Equal("2001:41d0:d00:d400::/56", options.Clusters[0].Ipv6Block);
    }

    // Terraform cannot hash a certificate's DER, so an environment may give the server certificate instead of its
    // fingerprint; the pin is the same SHA-256 of the DER that `incus remote add` prints.
    [Fact]
    public void ACertificateWithoutAFingerprint_IsPinnedByTheSha256OfItsDer()
    {
        using var certificate = SelfSigned();
        var cluster = Valid();
        cluster.ServerFingerprint = "";
        cluster.ServerCertificatePem = certificate.ExportCertificatePem();

        var options = ClusterOptions.FromConfiguration([cluster]);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), options.Clusters[0].ServerFingerprint);
    }

    [Fact]
    public void ACertificateAndItsOwnFingerprint_AreKept()
    {
        using var certificate = SelfSigned();
        var cluster = Valid();
        cluster.ServerCertificatePem = certificate.ExportCertificatePem();
        cluster.ServerFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));

        var options = ClusterOptions.FromConfiguration([cluster]);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), options.Clusters[0].ServerFingerprint);
    }

    [Fact]
    public void ACertificateAndAnotherFingerprint_StopsStartup_NamingTheFingerprint()
    {
        using var certificate = SelfSigned();
        var cluster = Valid();
        cluster.ServerCertificatePem = certificate.ExportCertificatePem();

        var ex = Assert.Throws<InvalidOperationException>(() => ClusterOptions.FromConfiguration([cluster]));

        Assert.Contains("Clusters:0:ServerFingerprint", ex.Message);
    }

    [Fact]
    public void AnUnreadableCertificate_StopsStartup_NamingTheCertificate()
    {
        var cluster = Valid();
        cluster.ServerFingerprint = "";
        cluster.ServerCertificatePem = "-----BEGIN CERTIFICATE-----\nnot a certificate\n-----END CERTIFICATE-----";

        var ex = Assert.Throws<InvalidOperationException>(() => ClusterOptions.FromConfiguration([cluster]));

        Assert.Contains("Clusters:0:ServerCertificatePem", ex.Message);
    }

    private static X509Certificate2 SelfSigned()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=singapore01", key, HashAlgorithmName.SHA384);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public static TheoryData<string, Action<ClusterConfig>> Refused => new()
    {
        { "Endpoint", c => c.Endpoint = "http://localhost:8443" },
        { "Endpoint", c => c.Endpoint = "" },
        { "ServerFingerprint", c => c.ServerFingerprint = "abc" },
        { "ServerFingerprint", c => c.ServerFingerprint = "" },
        { "Ipv6Block", c => c.Ipv6Block = "2001:41d0:d00:d400::/64" },
        { "Ipv6Block", c => c.Ipv6Block = "2001:41d0:d00:d401::/56" },
        { "Ipv6Block", c => c.Ipv6Block = "10.0.0.0/8" },
        { "OvnUplinkNetwork", c => c.OvnUplinkNetwork = new string('u', 65) },
        { "StoragePool", c => c.StoragePool = new string('p', 65) },
        { "Id", c => c.Id = " " },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AValueTheClusterCannotBeReachedWith_StopsStartup_NamingTheField(string field, Action<ClusterConfig> spoil)
    {
        var cluster = Valid();
        spoil(cluster);

        var ex = Assert.Throws<InvalidOperationException>(() => ClusterOptions.FromConfiguration([Valid("local-mac"), cluster]));

        Assert.Contains($"Clusters:1:{field}", ex.Message);
    }

    [Fact]
    public void TheSameIdTwice_StopsStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ClusterOptions.FromConfiguration([Valid(), Valid()]));

        Assert.Contains("singapore01", ex.Message);
    }

    [Fact]
    public void ClusterOf_AConfiguredCluster_IsThatCluster()
    {
        var options = ClusterOptions.FromConfiguration([Valid("local-mac"), Valid()]);

        Assert.Equal("singapore01", options.ClusterOf("singapore01").Id);
    }

    [Fact]
    public void ClusterOf_AnUnknownId_IsClusterNotFound()
    {
        var options = ClusterOptions.FromConfiguration([Valid()]);

        var ex = Assert.Throws<NotFoundException>(() => options.ClusterOf("mars01"));

        Assert.Equal("CLUSTER_NOT_FOUND", ex.Code);
    }
}
