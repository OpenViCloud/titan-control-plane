using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Api;

/// <summary>
/// The scheduled jobs run only when their CronJob calls POST /internal/jobs/{job}: a job missing from Program.cs fails
/// every pass with 404, and no service-level test builds the host. And no pod runs a job on a timer of its own
/// (titan-ops architecture/platform-infra.md#background-jobs).
/// </summary>
// FullHost — its own CI process, for the reason CatalogContractTests gives.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ScheduledJobHostWiringTests : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    private readonly OpenApiSpecTests.ApiFactory _factory;

    public ScheduledJobHostWiringTests(OpenApiSpecTests.ApiFactory factory)
    {
        _factory = factory;
    }

    private static readonly string[] JobNames =
    [
        "backup-schedule", "backup-status", "template-sync-never-asked", "idempotency-cleanup",
        "provider-command-retention", "vm-metric-retention", "usage-record-retention", "meter",
    ];

    [Fact]
    public void EveryScheduledJob_ResolvesOnce_ByItsName()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        List<string> names = scope.ServiceProvider.GetServices<IScheduledJob>().Select(j => j.Name).ToList();

        Assert.Equal(JobNames.Order(), names.Order());
    }

    // The hosted services left are the wallet's (a subscription and a 30-second sweeper: no CronJob runs that often) and
    // the IPv6 route seed; Titan's own, by type.
    [Fact]
    public void NoScheduledJob_RunsOnAPodTimer()
    {
        HashSet<string> titanHostedServices = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("Titan."))
            .Select(t => t.Name)
            .ToHashSet();

        // A subset: the subscriber is there only when NATS is configured, which this host is not.
        Assert.Subset(
            new HashSet<string> { "ClusterIpv6RouteSeedService", "WalletMutationSubscriber", "WalletMutationSweeper" },
            titanHostedServices);
        Assert.Contains("WalletMutationSweeper", titanHostedServices);
    }
}
