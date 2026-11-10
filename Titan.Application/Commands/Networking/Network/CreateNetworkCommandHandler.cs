using TenantAggregate = Titan.Domain.Aggregates.Tenant.Tenant;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Services.Platform.Tenant;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Services;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;

namespace Titan.Application.Commands.Networking.Network;

public class CreateNetworkCommandHandler(
    INetworkRepository repo,
    NetworkRoutes routes,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ClusterOptions clusters)
{
    public async Task<CreateNetworkCommandResult> HandleAsync(CreateNetworkCommand command, CancellationToken ct = default)
    {
        var tenantId = currentUser.RequireTenantId();
        var tenant = await tenantRepo.FindByIdAsync(tenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        var activeNetworkCount = await repo.CountActiveByTenantAsync(tenantId, ct);

        // The validator already answered 400 for a bad shape; these are the Domain's own words for the same rules.
        NetworkName name = NetworkName.Parse(command.Name);
        Ipv4Cidr? requestedCidr = null;
        if (command.Cidr != null)
        {
            requestedCidr = Ipv4Cidr.Parse(command.Cidr);
        }

        if (await repo.ExistsByNameAsync(tenantId, name, ct))
            throw new ConflictException("NETWORK_NAME_TAKEN", $"Network name '{command.Name}' is already taken.");

        if (requestedCidr != null && await repo.HasCidrOverlapAsync(tenantId, requestedCidr, ct))
            throw new BusinessException("NETWORK_CIDR_OVERLAP", $"CIDR '{command.Cidr}' overlaps with an existing network.");

        ClusterConfig cluster = clusters.ClusterOf(command.ClusterId); // titan-ops features/catalog/design.md#cluster-is-a-real-choice
        int activePublicIpv6Count = 0;
        if (command.EnablePublicIpv6)
        {
            if (!cluster.HasPublicIpv6)
            {
                throw new BusinessException("IPV6_NOT_AVAILABLE", $"Cluster '{command.ClusterId}' has no public IPv6.");
            }
            activePublicIpv6Count = await repo.CountPublicIpv6ActiveByTenantAsync(tenantId, ct);
        }

        // Quota comes after the name/CIDR checks above.
        TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.Network, activeNetworkCount);
        if (command.EnablePublicIpv6)
        {
            TenantQuotaGate.EnsureHasRoom(tenant, TenantQuotaKind.PublicIpv6, activePublicIpv6Count);
        }
        var network = NetworkAggregate.Create(tenant.Id, name, command.ClusterId,
            requestedCidr, command.EnablePublicIpv6, command.DefaultIngressPolicy, command.DefaultEgressPolicy);
        string? routerAddress = null;
        if (command.EnablePublicIpv6)
        {
            // The route and the network commit in one save; the router address goes out once, in network.create.
            routerAddress = await routes.GiveAsync(network, cluster.Ipv6Block!, ct);
        }
        repo.Add(network);
        activity.Stage("network.create_requested", "network", network.Id, network.Name.Value); // atomic with the network insert
        await commands.EnqueueNetworkCreateAsync(network, network.Operation.PendingId!.Value,
            TenantAggregate.ProjectNameFor(tenant.Id), routerAddress, ct);
        await repo.SaveAsync(ct);

        return new CreateNetworkCommandResult(network.Id);
    }
}
