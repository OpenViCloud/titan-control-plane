using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Api;

/// <summary>
/// The scheduled jobs run only when their CronJob (or, for the IPv6 route seed, the PostSync hook Job) calls
/// POST /internal/jobs/{job}: a job missing from Program.cs fails every pass with 404, and no service-level test builds
/// the host. And no pod runs a job on a timer of its own
/// (titan-ops architecture/background-jobs.md#background-jobs).
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
        "provider-command-retention", "vm-metric-retention", "usage-record-retention", "meter", "ipv6-route-seed",
        "wallet-sweep",
    ];

    [Fact]
    public void EveryScheduledJob_ResolvesOnce_ByItsName()
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        List<string> names = scope.ServiceProvider.GetServices<IScheduledJob>().Select(j => j.Name).ToList();

        Assert.Equal(JobNames.Order(), names.Order());
    }

    // The one hosted service left is the wallet's NATS subscription, which listens rather than runs on a timer; the
    // wallet sweeper is a scheduled job like the others. Titan's own, by type.
    [Fact]
    public void NoScheduledJob_RunsOnAPodTimer()
    {
        HashSet<string> titanHostedServices = _factory.Services.GetServices<IHostedService>()
            .Select(s => s.GetType())
            .Where(t => t.Namespace != null && t.Namespace.StartsWith("Titan."))
            .Select(t => t.Name)
            .ToHashSet();

        // A subset: the subscriber is there only when NATS is configured, which this host is not.
        Assert.Subset(new HashSet<string> { "WalletMutationSubscriber" }, titanHostedServices);
        Assert.DoesNotContain("WalletMutationSweeper", titanHostedServices);
    }
}
