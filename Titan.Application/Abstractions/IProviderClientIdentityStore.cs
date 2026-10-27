namespace Titan.Application.Abstractions;

// The public half of Titan's client identity: what an admin adds to a cluster's trust store
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#provider-client-identity). The private key never leaves Infrastructure.
public record ProviderClientCertificate(string CertificatePem, string Fingerprint);

public interface IProviderClientIdentityStore
{
    /// The certificate of the configured identity.
    Task<ProviderClientCertificate> GetCertificateAsync(CancellationToken ct);
}
