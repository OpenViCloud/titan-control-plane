using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;

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
        // Locked, so two deletes take turns and the second sees the first waiting
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        var loadBalancer = await ComputeOwnership.RequireLoadBalancerForUpdateAsync(repo, callerId, command.LoadBalancerId, ct);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): this delete still waits, or the load
        // balancer is already deleted. A delete that failed is not a repeat: it falls through and the aggregate decides.
        // Answer like the first request: nothing recorded, nothing sent.
        bool deleteIsWaiting = loadBalancer.Transition == LoadBalancerTransition.Deleting;
        bool alreadyRequestedOrDone = deleteIsWaiting || loadBalancer.IsTerminal;
        if (alreadyRequestedOrDone)
        {
            return;
        }

        // The create failed before Incus had it (titan-ops architecture/desired-and-current.md#delete-locally).
        bool neverReachedIncus = loadBalancer.ExternalId is null && loadBalancer.Transition == null;
        if (neverReachedIncus)
        {
            loadBalancer.DeleteLocally();
            activity.Stage("load_balancer.delete_requested", "load_balancer", loadBalancer.Id, loadBalancer.Name);
            activity.StageOutcome(loadBalancer.TenantId, "load_balancer.deleted", "load_balancer", loadBalancer.Id, loadBalancer.Name);
            await repo.SaveAsync(ct);
            return;
        }

        RequestTicket ticket = loadBalancer.RequestDelete();
        activity.Stage("load_balancer.delete_requested", "load_balancer", loadBalancer.Id, loadBalancer.Name);
        await commands.EnqueueLoadBalancerDeleteAsync(loadBalancer.Id, ticket.RequestId, loadBalancer.ExternalNetworkId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(loadBalancer.TenantId), loadBalancer.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
