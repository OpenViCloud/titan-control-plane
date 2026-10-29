using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Incus;

// Singleton: Titan's client identity is read from its Secret and the TLS client certificate is loaded once per
// process, shared by every Incus call (titan-ops features/cluster/design.md#provider-client-identity).
public sealed class ProviderClientIdentityStore(IOptions<ProviderClientIdentityOptions> options) : IProviderClientIdentityStore
{
    private readonly Lazy<LoadedIdentity> _loaded = new(() => Load(options.Value));

    public Task<ProviderClientCertificate> GetCertificateAsync(CancellationToken ct)
    {
        LoadedIdentity identity = _loaded.Value;
        return Task.FromResult(new ProviderClientCertificate(identity.CertificatePem, identity.Fingerprint));
    }

    /// Shared across requests: callers must not dispose it.
    public Task<X509Certificate2> GetClientCertificateAsync(CancellationToken ct) =>
        Task.FromResult(_loaded.Value.ClientCertificate);

    /// Only for the worker's connection route (titan-ops features/cluster/design.md#worker-cluster-connection): the private
    /// key leaves the control plane here and nowhere else.
    public Task<ProviderClientKeyPair> GetKeyPairAsync(CancellationToken ct)
    {
        LoadedIdentity identity = _loaded.Value;
        return Task.FromResult(new ProviderClientKeyPair(identity.CertificatePem, identity.KeyPem));
    }

    private static LoadedIdentity Load(ProviderClientIdentityOptions identity)
    {
        // Startup validation refuses a broken identity; an empty one is allowed only while no cluster is configured.
        string? problem = ProviderClientIdentityOptions.ProblemWith(identity, clustersConfigured: true);
        if (problem != null)
        {
            throw new ServiceUnavailableException("CLUSTER_IDENTITY_UNREADABLE", problem);
        }
        using X509Certificate2 withEphemeralKey = X509Certificate2.CreateFromPem(identity.CertificatePem, identity.KeyPem);
        // Round-trip through PKCS#12: TLS on macOS refuses an ephemeral key as a client certificate.
        X509Certificate2 loaded = X509CertificateLoader.LoadPkcs12(withEphemeralKey.Export(X509ContentType.Pkcs12), null);
        string fingerprint = Convert.ToHexStringLower(SHA256.HashData(loaded.RawData));
        return new LoadedIdentity(identity.CertificatePem, identity.KeyPem, fingerprint, loaded);
    }

    private sealed record LoadedIdentity(string CertificatePem, string KeyPem, string Fingerprint, X509Certificate2 ClientCertificate);
}

public record ProviderClientKeyPair(string CertificatePem, string KeyPem);
