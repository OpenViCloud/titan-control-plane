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

        // A repeat is answered like the first request: nothing recorded, nothing sent
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat). A delete that failed is not a
        // repeat: it falls through and the aggregate decides.
        if (loadBalancer.IsDeletedOrDeleting)
        {
            return;
        }

        // A load balancer that never reached Incus is deleted here and gets no ticket
        // (titan-ops architecture/desired-and-current.md#delete-locally): the request and its outcome at once.
        RequestTicket? ticket = loadBalancer.RequestDelete();
        activity.Stage("load_balancer.delete_requested", "load_balancer", loadBalancer.Id, loadBalancer.Name);
        if (ticket == null)
        {
            activity.StageOutcome(loadBalancer.TenantId, "load_balancer.deleted", "load_balancer", loadBalancer.Id, loadBalancer.Name);
            await repo.SaveAsync(ct);
            return;
        }
        await commands.EnqueueLoadBalancerDeleteAsync(loadBalancer.Id, ticket.RequestId, loadBalancer.ExternalNetworkId, ticket.ProviderId,
            TenantAggregate.ProjectNameFor(loadBalancer.TenantId), loadBalancer.ClusterId, ct);
        await repo.SaveAsync(ct);
    }
}
