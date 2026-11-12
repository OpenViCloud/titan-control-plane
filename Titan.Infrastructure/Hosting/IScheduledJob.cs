namespace Titan.Infrastructure.Hosting;

/// A background job that runs one pass when its Kubernetes CronJob calls POST /internal/jobs/{job}, so one control plane
/// pod runs each pass however many pods there are (titan-ops architecture/platform-infra.md#background-jobs). No pod runs it
/// on a timer of its own.
public interface IScheduledJob
{
    /// The {job} of the route and of the CronJob that calls it.
    string Name { get; }

    Task RunAsync(CancellationToken ct);
}
