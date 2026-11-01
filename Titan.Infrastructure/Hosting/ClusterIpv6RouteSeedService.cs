using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titan.Application.Options;
using Titan.Domain.Repositories;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Gives every cluster with public IPv6 a row for each of its static routes, once, when the control plane starts. The
/// cluster list is config, so a new cluster means a restart.
/// A failure leaves the pod running and tries again: a cluster that already has its rows loses nothing, and only a new
/// one answers IPV6_NOT_AVAILABLE meanwhile.
/// </summary>
public class ClusterIpv6RouteSeedService(
    IServiceScopeFactory scopeFactory,
    ClusterOptions clusters,
    ILogger<ClusterIpv6RouteSeedService> logger) : BackgroundService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RetryInterval);
        do
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await RunOnceAsync(scope.ServiceProvider.GetRequiredService<IClusterIpv6RouteRepository>(), clusters, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not make the IPv6 routes of the clusters; trying again");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public static async Task RunOnceAsync(IClusterIpv6RouteRepository routes, ClusterOptions clusters, CancellationToken ct)
    {
        foreach (var cluster in clusters.Clusters)
        {
            bool hasPublicIpv6 = cluster.OvnUplinkNetwork != null && cluster.Ipv6Block != null;
            if (hasPublicIpv6)
            {
                await routes.EnsureRoutesAsync(cluster.Id, ct);
            }
        }
    }
}
