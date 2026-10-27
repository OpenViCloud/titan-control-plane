using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Incus;

// Live reads of a cluster's Incus for the public catalog (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-incus).
// The route is anonymous: a failure keeps its code but loses its detail (endpoint, fingerprints), which goes to the
// log for the operator.
public sealed class IncusCatalogProvider(
    IncusClientFactory factory, ClusterOptions clusters, ILogger<IncusCatalogProvider> logger)
    : IComputeCatalogProvider
{
    private const long BytesPerGib = 1024L * 1024 * 1024;

    // Cluster-less, as the route is: the first configured cluster, which is only right while there is one cluster.
    public Task<List<OsImageInfo>> ListOsImagesAsync(CancellationToken ct = default)
    {
        ClusterConfig? firstCluster = clusters.Clusters.FirstOrDefault();
        if (firstCluster == null)
        {
            throw new NotFoundException("CLUSTER_NOT_FOUND", "No cluster is configured.");
        }
        return WithoutDetailAsync(firstCluster.Id, () => ReadOsImagesAsync(firstCluster.Id, ct));
    }

    private async Task<List<OsImageInfo>> ReadOsImagesAsync(string clusterId, CancellationToken ct)
    {
        using IncusClient client = await ConnectAsync(clusterId, ct);
        await client.RequireTrustedAsync(ct);
        List<IncusImage> images = await client.GetImagesAsync(ct);
        return images
            .Where(image => image.Type == "virtual-machine")
            .Select(image => new OsImageInfo(
                image.Fingerprint,
                image.Description ?? image.Fingerprint,
                (int)((image.Size + BytesPerGib - 1) / BytesPerGib)))
            .ToList();
    }

    private async Task<T> WithoutDetailAsync<T>(string clusterId, Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (ServiceUnavailableException e)
        {
            logger.LogWarning("Incus of cluster {ClusterId} failed ({Code}): {Detail}", clusterId, e.Code, e.Message);
            throw new ServiceUnavailableException(e.Code, $"Cluster '{clusterId}' is not available ({e.Code}).");
        }
    }

    private async Task<IncusClient> ConnectAsync(string clusterId, CancellationToken ct)
    {
        var cluster = clusters.ClusterOf(clusterId);
        return await factory.CreateAsync(cluster.Endpoint, cluster.ServerFingerprint, ct);
    }
}
