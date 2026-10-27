using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using Titan.Application.Options;
using Titan.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure.Incus;

internal static class IncusTestKit
{
    public const string AnyFingerprint = "ffcf7803b2371e90834c2dd2a454f48bcb9c541b16dd7dc54a93a104b1aa108f";

    // Shaped like the identity an operator puts in the Secret: ECDSA P-384, self-signed, client auth.
    public static ProviderClientIdentityOptions NewIdentity()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var request = new CertificateRequest("CN=titan", key, HashAlgorithmName.SHA384);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], critical: false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        return new ProviderClientIdentityOptions
        {
            CertificatePem = cert.ExportCertificatePem() + "\n",
            KeyPem = key.ExportPkcs8PrivateKeyPem(),
        };
    }

    // A fresh store each time: a store built here behaves like a newly started control plane.
    public static ProviderClientIdentityStore IdentityStore(ProviderClientIdentityOptions? identity = null) =>
        new(Options.Create(identity ?? NewIdentity()));

    public static ClusterOptions Clusters(params string[] ids) =>
        new() { Clusters = ids.Select(id => Cluster(id, $"https://{id}.invalid:8443", AnyFingerprint)).ToList() };

    public static ClusterConfig Cluster(string id, string endpoint, string serverFingerprint) =>
        new() { Id = id, Name = id, Endpoint = endpoint, ServerFingerprint = serverFingerprint };
}
