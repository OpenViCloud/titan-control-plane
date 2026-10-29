using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute;
using Titan.Application.Services.Compute.LoadBalancer;
using Titan.Domain.Aggregates.Compute.LoadBalancer;
using Titan.Domain.Repositories.Compute;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.LoadBalancer;

public class UpdateLoadBalancerCommandHandler(
    ILoadBalancerRepository repo,
    IVmRepository vmRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task HandleAsync(UpdateLoadBalancerCommand command, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var loadBalancer = await ComputeOwnership.RequireLoadBalancerAsync(repo, callerId, command.LoadBalancerId, ct);
        var listeners = LoadBalancerRequest.Listeners(command.Listeners);
        List<Guid> backendVmIds = command.Backends.Select(b => b.VmId).ToList();

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): the configuration already asked for,
        // whether Incus has it or the request still waits. Answer like the first request: nothing recorded, nothing
        // sent — the waiting command, if any, already carries it.
        bool notBeingDeleted = loadBalancer.Transition != LoadBalancerTransition.Deleting && !loadBalancer.IsTerminal;
        bool alreadyRequestedOrDone = notBeingDeleted && loadBalancer.HasDesiredSpec(listeners, backendVmIds);
        if (alreadyRequestedOrDone)
        {
            return;
        }

        var backends = await LoadBalancerRequest.BackendsAsync(vmRepo, callerId, command.Backends, ct);
        Guid intentId = loadBalancer.RequestUpdate(listeners, backends);
        activity.Stage("load_balancer.update_requested", "load_balancer", loadBalancer.Id, loadBalancer.Name);
        await commands.EnqueueLoadBalancerUpdateAsync(loadBalancer, intentId,
            TenantAggregate.ProjectNameFor(loadBalancer.TenantId), ct);
        await repo.SaveAsync(ct);
    }
}
