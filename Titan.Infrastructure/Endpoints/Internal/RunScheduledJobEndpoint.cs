using System.Diagnostics;
using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Titan.Infrastructure.Hosting;

namespace Titan.Infrastructure.Endpoints.Internal;

public class RunScheduledJobRequest
{
    public string Job { get; set; } = "";
}

/// Called by each job's Kubernetes CronJob, which waits for the pass to end: 204 when it ran, 404 for a name no job has,
/// and the pass's own error otherwise, so the CronJob's Job fails with it
/// (titan-ops architecture/background-jobs.md#background-jobs).
public class RunScheduledJobEndpoint(
    IEnumerable<IScheduledJob> jobs,
    IConfiguration config,
    ILogger<RunScheduledJobEndpoint> logger) : Endpoint<RunScheduledJobRequest>
{
    public override void Configure()
    {
        Post("internal/jobs/{job}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(RunScheduledJobRequest req, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        IScheduledJob? job = jobs.FirstOrDefault(j => j.Name == req.Job);
        if (job == null)
        {
            await SendNotFoundAsync(ct);
            return;
        }

        var watch = Stopwatch.StartNew();
        await job.RunAsync(ct);
        logger.LogInformation("Scheduled job {Job} ran in {DurationMs} ms", job.Name, watch.ElapsedMilliseconds);
        await SendNoContentAsync(ct);
    }
}
