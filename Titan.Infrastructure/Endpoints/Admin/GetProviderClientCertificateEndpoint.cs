using FastEndpoints;
using Titan.Application.Abstractions;

namespace Titan.Infrastructure.Endpoints.Admin;

// What an admin adds to an Incus cluster's trust store (`incus config trust add-certificate`): Titan's public
// certificate and its fingerprint, never the key (titan-ops features/cluster/design.md#provider-client-identity).
public class GetProviderClientCertificateEndpoint(IProviderClientIdentityStore identity)
    : EndpointWithoutRequest<ProviderClientCertificate>
{
    public override void Configure()
    {
        Get("v1/admin/clusters/client-certificate");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var certificate = await identity.GetCertificateAsync(ct);
        await SendAsync(certificate, cancellation: ct);
    }
}
