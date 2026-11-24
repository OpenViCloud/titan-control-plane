using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// BackfillDefaultNetworksCommandHandler as a job; its CronJob is suspended, run by hand (titan-ops features/network/design.md#default-network).
/// </summary>
public class DefaultNetworkBackfillService(BackfillDefaultNetworksCommandHandler handler) : IScheduledJob
{
    public string Name => BackfillDefaultNetworksCommandHandler.Job;

    public Task RunAsync(CancellationToken ct) => handler.HandleAsync(new BackfillDefaultNetworksCommand(), ct);
}
