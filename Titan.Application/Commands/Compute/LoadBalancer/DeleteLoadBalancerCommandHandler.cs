using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.Compute.LoadBalancer;
using Titan.Domain.Repositories.Compute;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.LoadBalancer;

public class DeleteLoadBalancerCommandHandler(
    ILoadBalancerRepository repo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteLoadBalancerCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var loadBalancer = await ComputeOwnership.RequireLoadBalancerAsync(repo, callerId, command.LoadBalancerId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat): this delete still waits, or the load
        // balancer is already deleted. Answer like the first request.
        bool deleteIsWaiting = loadBalancer.Transition == LoadBalancerTransition.Deleting;
        bool alreadyRequestedOrDone = deleteIsWaiting || loadBalancer.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        // The create failed before Incus had it (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#delete-locally).
        bool neverReachedIncus = loadBalancer.ExternalId is null && loadBalancer.Transition == null;
        if (neverReachedIncus)
        {
            loadBalancer.DeleteLocally();
            activity.Stage("load_balancer.delete_requested", "load_balancer", loadBalancer.Id, loadBalancer.Name);
            activity.StageOutcome(loadBalancer.TenantId, "load_balancer.deleted", "load_balancer", loadBalancer.Id, loadBalancer.Name);
            await repo.SaveAsync(ct);
            return;
        }

        string vip = loadBalancer.RequestDelete();
        activity.Stage("load_balancer.delete_requested", "load_balancer", loadBalancer.Id, loadBalancer.Name);
        await commands.EnqueueLoadBalancerDeleteAsync(loadBalancer.Id, loadBalancer.ExternalNetworkId, vip,
            TenantAggregate.ProjectNameFor(loadBalancer.TenantId), loadBalancer.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
