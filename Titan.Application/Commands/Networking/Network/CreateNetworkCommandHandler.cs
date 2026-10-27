using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Networking;
using Titan.Domain.Repositories.Platform;
using Titan.Domain.Aggregates.Networking.Network;
using NetworkAggregate = Titan.Domain.Aggregates.Networking.Network.Network;

namespace Titan.Application.Commands.Networking.Network;

public class CreateNetworkCommandHandler(
    INetworkRepository repo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IActivityRecorder activity,
    ZoneOptions zones)
{
    public async Task<CreateNetworkCommandResult> HandleAsync(CreateNetworkCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the wire contract (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/code-structure.md#command-is-wire-contract)

        var tenantId = currentUser.RequireTenantId();
        var tenant = await tenantRepo.FindByIdAsync(tenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        var activeNetworkCount = await repo.CountActiveByTenantAsync(tenantId, ct);

        if (await repo.ExistsByNameAsync(tenantId, req.Name, ct))
            throw new ConflictException("NETWORK_NAME_TAKEN", $"Network name '{req.Name}' is already taken.");

        if (req.Cidr is not null && await repo.HasCidrOverlapAsync(tenantId, req.Cidr, ct))
            throw new BusinessException("NETWORK_CIDR_OVERLAP", $"CIDR '{req.Cidr}' overlaps with an existing network.");

        zones.Require(req.ZoneId); // https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#zone-is-a-real-choice
        NetworkPublicIpv6? publicIpv6 = null;
        int activePublicIpv6Count = 0;
        if (req.EnablePublicIpv6)
        {
            publicIpv6 = await FreePublicIpv6Async(req.ZoneId, ct);
            activePublicIpv6Count = await repo.CountPublicIpv6ActiveByTenantAsync(tenantId, ct);
        }

        // Quota refuses inside the factory, so it comes after the name/CIDR checks above.
        var network = NetworkAggregate.Create(tenant, activeNetworkCount, activePublicIpv6Count, req.Name, req.ZoneId,
            publicIpv6, req.DefaultIngressPolicy, req.DefaultEgressPolicy);
        repo.Add(network);
        activity.Stage("network.create_requested", "network", network.Id, network.Name); // atomic with the network insert
        await commands.EnqueueNetworkCreateAsync(network.Id, network.Name, network.ZoneId, tenant.ProjectName, req.Cidr,
            network.DefaultIngressPolicy.ToString(), network.DefaultEgressPolicy.ToString(),
            network.PublicIpv6Subnet, network.PublicIpv6RouterAddress, ct);
        await repo.SaveAsync(ct);

        return new CreateNetworkCommandResult(network.Id);
    }

    // A zone has public IPv6 when its cluster names both the uplink and the /56 (https://github.com/OpenViCloud/titan-ops/blob/main/docs/infra/incus/design.md#ipv6-address-plan).
    // Two creates that pick the same free /64 at once meet at the unique index, not here.
    private async Task<NetworkPublicIpv6> FreePublicIpv6Async(string zoneId, CancellationToken ct)
    {
        var cluster = zones.ClusterOf(zoneId);
        string? block = cluster.Ipv6Block;
        string? uplink = cluster.OvnUplinkNetwork;
        if (block == null || uplink == null)
        {
            throw new BusinessException("IPV6_NOT_AVAILABLE", $"Zone '{zoneId}' has no public IPv6.");
        }
        var taken = await repo.ListPublicIpv6IndexesInZoneAsync(zoneId, ct);
        var released = await repo.ListPublicIpv6ReleasesInZoneAsync(zoneId, ct);
        var free = NetworkPublicIpv6.FirstFree(block, taken, released);
        if (free == null)
        {
            throw new BusinessException("IPV6_NOT_AVAILABLE", $"Zone '{zoneId}' has no free public IPv6 /64 left.");
        }
        return free;
    }
}
