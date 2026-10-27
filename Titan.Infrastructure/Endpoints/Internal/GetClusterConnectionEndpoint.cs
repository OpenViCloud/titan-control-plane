using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Options;
using Titan.Infrastructure.Incus;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker's way to a cluster's Incus (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#worker-cluster-connection): endpoint,
// the server fingerprint to pin, Titan's client certificate WITH its private key, and the cluster's provider settings. Behind X-Internal-Token only;
// the body is never logged.
public class GetClusterConnectionEndpoint(
    ClusterOptions clusters, ProviderClientIdentityStore identity, IConfiguration config)
    : EndpointWithoutRequest<ClusterConnectionResponse>
{
    public override void Configure()
    {
        Get("internal/clusters/{clusterId}/connection");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        bool callerIsTheWorker = InternalTokenAuth.Matches(token, config["Internal:Token"]);
        if (!callerIsTheWorker)
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        string clusterId = Route<string>("clusterId") ?? "";
        ClusterConfig cluster = clusters.ClusterOf(clusterId);
        ProviderClientKeyPair keyPair = await identity.GetKeyPairAsync(ct);
        await SendAsync(
            new ClusterConnectionResponse(cluster.Endpoint, cluster.ServerFingerprint, keyPair.CertificatePem, keyPair.KeyPem,
                cluster.OvnUplinkNetwork, cluster.StoragePool),
            cancellation: ct);
    }
}

public record ClusterConnectionResponse(
    string Endpoint,
    string ServerFingerprint,
    string ClientCertificatePem,
    string ClientKeyPem,
    // The cluster's provider settings (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-provider-settings); null = not set.
    string? OvnUplinkNetwork,
    string? StoragePool);
