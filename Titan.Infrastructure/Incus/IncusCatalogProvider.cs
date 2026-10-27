using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Incus;

// Live reads of a zone's Incus cluster for the public catalog (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-incus).
// Resolved per call, so an admin's edit of the cluster row takes effect on the next read.
// Both routes are anonymous: a failure keeps its code but loses its detail (endpoint, fingerprints), which goes to the
// log for the operator.
public sealed class IncusCatalogProvider(
    IncusClientFactory factory, ZoneOptions zones, ILogger<IncusCatalogProvider> logger)
    : IComputeCatalogProvider
{
    private const long BytesPerGib = 1024L * 1024 * 1024;

    public Task<List<ClusterInfo>> ListClustersAsync(string zoneId, CancellationToken ct = default) =>
        WithoutDetailAsync(zoneId, () => ReadClustersAsync(zoneId, ct));

    private async Task<List<ClusterInfo>> ReadClustersAsync(string zoneId, CancellationToken ct)
    {
        using IncusClient client = await ConnectAsync(zoneId, ct);
        IncusServerInfo server = await client.RequireTrustedAsync(ct);
        bool isClustered = await client.IsClusteredAsync(ct);
        if (!isClustered)
        {
            // A single server's /1.0/cluster carries server_name "" (lab); its name is the one /1.0 reports.
            return [new ClusterInfo(server.ServerName, server.ServerName)];
        }
        List<string> members = await client.GetClusterMemberNamesAsync(ct);
        return members.Select(name => new ClusterInfo(name, name)).ToList();
    }

    // Zone-less, as the route is: the first configured zone, which is only right while there is one cluster.
    public Task<List<OsImageInfo>> ListOsImagesAsync(CancellationToken ct = default)
    {
        ZoneConfig? firstZone = zones.Zones.FirstOrDefault();
        if (firstZone == null)
        {
            throw new NotFoundException("CLUSTER_NOT_FOUND", "No zone is configured.");
        }
        return WithoutDetailAsync(firstZone.Id, () => ReadOsImagesAsync(firstZone.Id, ct));
    }

    private async Task<List<OsImageInfo>> ReadOsImagesAsync(string zoneId, CancellationToken ct)
    {
        using IncusClient client = await ConnectAsync(zoneId, ct);
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

    private async Task<T> WithoutDetailAsync<T>(string zoneId, Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (ServiceUnavailableException e)
        {
            logger.LogWarning("Incus cluster of zone {ZoneId} failed ({Code}): {Detail}", zoneId, e.Code, e.Message);
            throw new ServiceUnavailableException(e.Code, $"The cluster of zone '{zoneId}' is not available ({e.Code}).");
        }
    }

    private async Task<IncusClient> ConnectAsync(string zoneId, CancellationToken ct)
    {
        var cluster = zones.ClusterOf(zoneId);
        return await factory.CreateAsync(cluster.Endpoint, cluster.ServerFingerprint, ct);
    }
}
