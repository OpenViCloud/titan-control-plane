using Titan.Application.Options;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Gives every cluster with public IPv6 a row for each of its static routes, adding only the missing ones. The cluster list
/// is config, and ArgoCD syncs whenever it changes: the control plane's PostSync hook Job calls this job after each sync, so
/// a new cluster gets its routes once its pods are up (titan-ops features/network/design.md#ipv6-public). A failure fails
/// the hook, so the sync shows it; the pods keep serving, and only a new cluster answers IPV6_NOT_AVAILABLE meanwhile.
/// </summary>
public class ClusterIpv6RouteSeedService(IClusterIpv6RouteRepository routes, ClusterOptions clusters) : IScheduledJob
{
    public const string JobName = "ipv6-route-seed";

    public string Name => JobName;

    public Task RunAsync(CancellationToken ct) => RunOnceAsync(routes, clusters, ct);

    public static async Task RunOnceAsync(IClusterIpv6RouteRepository routes, ClusterOptions clusters, CancellationToken ct)
    {
        foreach (var cluster in clusters.Clusters)
        {
            if (cluster.HasPublicIpv6)
            {
                await routes.EnsureRoutesAsync(cluster.Id, ct);
            }
        }
    }
}
