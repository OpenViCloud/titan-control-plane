using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// CleanFunctionCodeCommandHandler as a job: its CronJob calls it every 10 minutes, the schedule being the CronJob's
/// (titan-ops architecture/background-jobs.md#background-jobs).
/// </summary>
public class FunctionCodeCleanupService(CleanFunctionCodeCommandHandler handler) : IScheduledJob
{
    public string Name => CleanFunctionCodeCommandHandler.Job;

    public Task RunAsync(CancellationToken ct) => handler.HandleAsync(new CleanFunctionCodeCommand(), ct);
}
