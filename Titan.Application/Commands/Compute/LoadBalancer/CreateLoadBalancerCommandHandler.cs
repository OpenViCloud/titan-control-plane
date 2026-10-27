using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute.LoadBalancer;
using Titan.Application.Services.Networking.Network;
using Titan.Domain.Aggregates.Compute.LoadBalancer;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;
using Titan.Domain.Repositories.Networking;
using Titan.Domain.Repositories.Platform;
using LoadBalancerAggregate = Titan.Domain.Aggregates.Compute.LoadBalancer.LoadBalancer;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Application.Commands.Compute.LoadBalancer;

public class CreateLoadBalancerCommandHandler(
    ILoadBalancerRepository repo,
    INetworkRepository networkRepo,
    IVmRepository vmRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity)
{
    public async Task<CreateLoadBalancerCommandResult> HandleAsync(CreateLoadBalancerCommand req, CancellationToken ct = default)
    {
        var callerId = currentUser.RequireTenantId();
        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        if (await repo.ExistsByNameAsync(callerId, req.Name, ct))
        {
            throw new ConflictException("LOAD_BALANCER_NAME_TAKEN", $"A load balancer named '{req.Name}' already exists.");
        }

        var network = await NetworkOwnership.RequireNetworkAsync(networkRepo, callerId, req.NetworkId, ct);
        // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/load-balancer/design.md#lb-needs-public-ipv6: the VIP lives in the network's /64.
        string externalNetworkId = network.BeginLoadBalancer();
        string subnet = network.PublicIpv6Subnet!;

        var listeners = LoadBalancerRequest.Listeners(req.Listeners);
        var backends = await LoadBalancerRequest.BackendsAsync(vmRepo, callerId, req.Backends, ct);
        int activeCount = await repo.CountActiveByTenantAsync(callerId, ct);
        // Two creates that pick the same free number at once meet at the unique index, not here.
        var taken = await repo.ListVipIndexesOfNetworkAsync(network.Id, ct);
        var vip = LoadBalancerVip.FirstFree(subnet, taken)
            ?? throw new BusinessException("LOAD_BALANCER_INVALID_STATE", "This network has no free load balancer address left.");

        // Quota refuses inside the factory — see CreateVmCommandHandler.
        var loadBalancer = LoadBalancerAggregate.Create(tenant, activeCount, req.Name, network.Id, externalNetworkId,
            subnet, network.ClusterId, vip, listeners, backends);
        repo.Add(loadBalancer);
        activity.Stage("load_balancer.create_requested", "load_balancer", loadBalancer.Id, loadBalancer.Name);
        await commands.EnqueueLoadBalancerCreateAsync(loadBalancer, TenantAggregate.ProjectNameFor(loadBalancer.TenantId), ct);
        await repo.SaveAsync(ct);

        return new CreateLoadBalancerCommandResult(loadBalancer.Id);
    }
}
