using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Domain.Aggregates.ClusterIpv6Pool;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Aggregates.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Network.Network;
using PoolAggregate = Titan.Domain.Aggregates.ClusterIpv6Pool.ClusterIpv6Pool;

namespace Titan.Application.Commands.Networking.Network;

public class CreateNetworkCommandHandler(
    INetworkRepository repo,
    IClusterIpv6PoolRepository pools,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ClusterOptions clusters)
{
    public async Task<CreateNetworkCommandResult> HandleAsync(CreateNetworkCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the wire contract (titan-ops architecture/code-structure.md#command-is-wire-contract)

        var tenantId = currentUser.RequireTenantId();
        var tenant = await tenantRepo.FindByIdAsync(tenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        var activeNetworkCount = await repo.CountActiveByTenantAsync(tenantId, ct);

        // The validator already answered 400 for a bad shape; these are the Domain's own words for the same rules.
        NetworkName name = NetworkName.Parse(req.Name);
        Ipv4Cidr? requestedCidr = null;
        if (req.Cidr != null)
        {
            requestedCidr = Ipv4Cidr.Parse(req.Cidr);
        }

        if (await repo.ExistsByNameAsync(tenantId, name, ct))
            throw new ConflictException("NETWORK_NAME_TAKEN", $"Network name '{req.Name}' is already taken.");

        if (requestedCidr != null && await repo.HasCidrOverlapAsync(tenantId, requestedCidr, ct))
            throw new BusinessException("NETWORK_CIDR_OVERLAP", $"CIDR '{req.Cidr}' overlaps with an existing network.");

        clusters.ClusterOf(req.ClusterId); // titan-ops features/catalog/design.md#cluster-is-a-real-choice
        string? block56 = null;
        int activePublicIpv6Count = 0;
        if (req.EnablePublicIpv6)
        {
            block56 = Block56Of(req.ClusterId);
            activePublicIpv6Count = await repo.CountPublicIpv6ActiveByTenantAsync(tenantId, ct);
        }

        // Quota refuses inside the factory, so it comes after the name/CIDR checks above.
        var network = NetworkAggregate.Create(tenant, activeNetworkCount, activePublicIpv6Count, name, req.ClusterId,
            requestedCidr, req.EnablePublicIpv6, req.DefaultIngressPolicy, req.DefaultEgressPolicy);
        if (block56 != null)
        {
            await GiveBlock64Async(network, block56, ct);
        }
        repo.Add(network);
        activity.Stage("network.create_requested", "network", network.Id, network.Name.Value); // atomic with the network insert
        string? heldCidr = null;
        if (network.RequestedCidr != null)
        {
            heldCidr = network.RequestedCidr.Value;
        }
        await commands.EnqueueNetworkCreateAsync(network.Id, network.Name.Value, network.ClusterId, tenant.ProjectName, heldCidr,
            network.DefaultIngressPolicy.ToString(), network.DefaultEgressPolicy.ToString(),
            network.PublicIpv6Subnet, network.PublicIpv6RouterAddress, ct);
        await repo.SaveAsync(ct);

        return new CreateNetworkCommandResult(network.Id);
    }

    // A cluster has public IPv6 when its configuration names both the uplink and the /56 (titan-ops infra/incus/design.md#ipv6-address-plan).
    private string Block56Of(string clusterId)
    {
        var cluster = clusters.ClusterOf(clusterId);
        string? block56 = cluster.Ipv6Block;
        string? uplink = cluster.OvnUplinkNetwork;
        if (block56 == null || uplink == null)
        {
            throw new BusinessException("IPV6_NOT_AVAILABLE", $"Cluster '{clusterId}' has no public IPv6.");
        }
        return block56;
    }

    // The pool and the network commit in one save. Two creates that load the pool at once meet at its version, not here.
    private async Task GiveBlock64Async(NetworkAggregate network, string block56, CancellationToken ct)
    {
        PoolAggregate pool = await pools.FindOrAddAsync(network.ClusterId, ct);
        GrantedBlock64 granted = pool.GiveBlock64To(network.Id, block56);
        network.ReceivePublicIpv6(granted.Subnet, granted.RouterAddress);
    }
}
