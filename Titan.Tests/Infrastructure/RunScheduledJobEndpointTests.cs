using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Infrastructure;

// A Kubernetes CronJob calls this route once per schedule, so one control plane pod runs each pass of a job
// (titan-ops architecture/background-jobs.md#background-jobs).
public class RunScheduledJobEndpointTests
{
    private sealed class CountingJob(string name) : IScheduledJob
    {
        public string Name => name;
        public int Runs { get; private set; }

        public Task RunAsync(CancellationToken ct)
        {
            Runs++;
            return Task.CompletedTask;
        }
    }

    private readonly CountingJob _cleanup = new("idempotency-cleanup");
    private readonly CountingJob _retention = new("vm-metric-retention");

    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private RunScheduledJobEndpoint Endpoint(string? token)
    {
        var ep = Factory.Create<RunScheduledJobEndpoint>(
            ctx =>
            {
                if (token != null)
                {
                    ctx.Request.Headers["X-Internal-Token"] = token;
                }
            },
            new IScheduledJob[] { _cleanup, _retention }, ConfigWithToken("secret"),
            NullLogger<RunScheduledJobEndpoint>.Instance);
        return ep;
    }

    [Fact]
    public async Task ANamedJob_RunsOnce_And204()
    {
        RunScheduledJobEndpoint ep = Endpoint("secret");

        await ep.HandleAsync(new RunScheduledJobRequest { Job = "vm-metric-retention" }, CancellationToken.None);

        Assert.Equal(204, ep.HttpContext.Response.StatusCode);
        Assert.Equal(1, _retention.Runs);
        Assert.Equal(0, _cleanup.Runs);
    }

    [Fact]
    public async Task AnUnknownJob_Is404_AndRunsNothing()
    {
        RunScheduledJobEndpoint ep = Endpoint("secret");

        await ep.HandleAsync(new RunScheduledJobRequest { Job = "no-such-job" }, CancellationToken.None);

        Assert.Equal(404, ep.HttpContext.Response.StatusCode);
        Assert.Equal(0, _retention.Runs + _cleanup.Runs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task WithoutTheInternalToken_Is401_AndRunsNothing(string? token)
    {
        RunScheduledJobEndpoint ep = Endpoint(token);

        await ep.HandleAsync(new RunScheduledJobRequest { Job = "vm-metric-retention" }, CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Equal(0, _retention.Runs + _cleanup.Runs);
    }
}
