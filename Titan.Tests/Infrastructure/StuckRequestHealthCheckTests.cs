using Microsoft.Extensions.Diagnostics.HealthChecks;
using Titan.Application.Abstractions;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/resource-lifecycle.md#lost-and-stale: a request waiting past the threshold with its command row
// still open is a slow or absent worker (Degraded); with the row closed or missing nothing will ever end it (Unhealthy).
public class StuckRequestHealthCheckTests
{
    private static StuckRequest Stuck(string? commandStatus) =>
        new("vm", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Stop", DateTimeOffset.UtcNow.AddMinutes(-20), commandStatus);

    [Fact]
    public void NothingStuck_IsHealthy()
    {
        HealthCheckResult result = StuckRequestHealthCheck.Evaluate([], 15);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("Leased")]
    public void AStuckRequest_WhoseCommandIsStillOpen_IsDegraded(string commandStatus)
    {
        HealthCheckResult result = StuckRequestHealthCheck.Evaluate([Stuck(commandStatus)], 15);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("worker", result.Description);
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Failed")]
    [InlineData(null)]
    public void AStuckRequest_WhoseCommandIsClosedOrMissing_IsUnhealthy(string? commandStatus)
    {
        HealthCheckResult result = StuckRequestHealthCheck.Evaluate([Stuck(commandStatus)], 15);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("/v1/admin/requests/stuck", result.Description);
    }

    [Fact] // worst first: one request nobody will end outranks any number the worker is still on
    public void AClosedRow_OutranksOpenOnes()
    {
        HealthCheckResult result = StuckRequestHealthCheck.Evaluate([Stuck("Pending"), Stuck("Done"), Stuck("Leased")], 15);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.StartsWith("1 request(s)", result.Description);
    }
}
