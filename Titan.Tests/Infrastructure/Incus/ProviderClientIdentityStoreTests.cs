using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure.Incus;

// Titan's client identity toward Incus comes from a Secret (titan-ops features/cluster/design.md#provider-client-identity):
// the control plane never generates one, so a restart, a second replica or a rebuilt database all present the
// certificate every cluster already trusts.
public class ProviderClientIdentityStoreTests
{
    [Fact]
    public async Task TheClientCertificate_IsTheConfiguredOne_WithItsPrivateKey()
    {
        var identity = IncusTestKit.NewIdentity();
        var store = IncusTestKit.IdentityStore(identity);

        X509Certificate2 cert = await store.GetClientCertificateAsync(CancellationToken.None);

        Assert.True(cert.HasPrivateKey);
        using var configured = X509Certificate2.CreateFromPem(identity.CertificatePem);
        Assert.Equal(configured.RawData, cert.RawData);
    }

    [Fact]
    public async Task TheFingerprint_IsTheSha256OfTheCertificate()
    {
        var store = IncusTestKit.IdentityStore();

        var publicHalf = await store.GetCertificateAsync(CancellationToken.None);

        using var cert = X509Certificate2.CreateFromPem(publicHalf.CertificatePem);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(cert.RawData)), publicHalf.Fingerprint);
    }

    [Fact]
    public async Task ThePublicHalf_CarriesNoPrivateKey()
    {
        var store = IncusTestKit.IdentityStore();

        var publicHalf = await store.GetCertificateAsync(CancellationToken.None);

        Assert.DoesNotContain("PRIVATE KEY", publicHalf.CertificatePem);
    }

    // Two stores over the same Secret stand in for a restarted control plane or a second replica.
    [Fact]
    public async Task TheSameSecret_IsTheSameIdentity_AfterARestart()
    {
        var identity = IncusTestKit.NewIdentity();

        var first = await IncusTestKit.IdentityStore(identity).GetCertificateAsync(CancellationToken.None);
        var afterRestart = await IncusTestKit.IdentityStore(identity).GetCertificateAsync(CancellationToken.None);

        Assert.Equal(first.Fingerprint, afterRestart.Fingerprint);
    }

    // Built once per process: every catalog request would otherwise load a new certificate.
    [Fact]
    public async Task TheClientCertificate_IsBuiltOnce_AndShared()
    {
        var store = IncusTestKit.IdentityStore();

        X509Certificate2 first = await store.GetClientCertificateAsync(CancellationToken.None);
        X509Certificate2 second = await store.GetClientCertificateAsync(CancellationToken.None);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task TheKeyPair_IsWhatTheSecretHolds()
    {
        var identity = IncusTestKit.NewIdentity();

        var pair = await IncusTestKit.IdentityStore(identity).GetKeyPairAsync(CancellationToken.None);

        Assert.Equal(identity.CertificatePem, pair.CertificatePem);
        Assert.Equal(identity.KeyPem, pair.KeyPem);
    }

    // With no cluster configured the control plane may start without an identity; asking for it is then a named failure.
    [Fact]
    public async Task NoIdentityConfigured_IsANamedFailure_NotA500()
    {
        var store = new ProviderClientIdentityStore(Options.Create(new ProviderClientIdentityOptions()));

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            store.GetCertificateAsync(CancellationToken.None));

        Assert.Equal("CLUSTER_IDENTITY_UNREADABLE", error.Code);
    }

    [Fact]
    public void Validation_AValidIdentity_Passes()
    {
        string? problem = ProviderClientIdentityOptions.ProblemWith(IncusTestKit.NewIdentity(), clustersConfigured: true);

        Assert.Null(problem);
    }

    [Fact]
    public void Validation_NoIdentity_AndNoCluster_Passes()
    {
        string? problem = ProviderClientIdentityOptions.ProblemWith(new ProviderClientIdentityOptions(), clustersConfigured: false);

        Assert.Null(problem);
    }

    // A cluster cannot be reached without Titan's certificate: refusing to start beats every Incus call failing.
    [Fact]
    public void Validation_AClusterButNoIdentity_StopsStartup()
    {
        string? problem = ProviderClientIdentityOptions.ProblemWith(new ProviderClientIdentityOptions(), clustersConfigured: true);

        Assert.NotNull(problem);
        Assert.Contains("ProviderClientIdentity", problem);
    }

    [Fact]
    public void Validation_AKeyOfAnotherCertificate_StopsStartup()
    {
        var identity = IncusTestKit.NewIdentity();
        identity.KeyPem = IncusTestKit.NewIdentity().KeyPem;

        string? problem = ProviderClientIdentityOptions.ProblemWith(identity, clustersConfigured: true);

        Assert.NotNull(problem);
    }

    [Fact]
    public void Validation_HalfAnIdentity_StopsStartup_EvenWithoutACluster()
    {
        var identity = IncusTestKit.NewIdentity();
        identity.KeyPem = "";

        string? problem = ProviderClientIdentityOptions.ProblemWith(identity, clustersConfigured: false);

        Assert.NotNull(problem);
    }

    // The problem text is logged at startup: it names the setting, never the key.
    [Fact]
    public void Validation_TheProblem_NeverCarriesTheKey()
    {
        var identity = IncusTestKit.NewIdentity();
        identity.CertificatePem = "not a certificate";

        string? problem = ProviderClientIdentityOptions.ProblemWith(identity, clustersConfigured: true);

        Assert.NotNull(problem);
        Assert.DoesNotContain("PRIVATE KEY", problem);
    }
}
