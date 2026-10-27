using Titan.Application.Abstractions;
using Titan.Application.Options;

namespace Titan.Infrastructure.Incus;

// The configuration is the whole list (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-from-config).
public class AdminClusterService(ZoneOptions zones) : IAdminClusterService
{
    public Task<List<AdminClusterDto>> ListForAdminAsync(CancellationToken ct)
    {
        var list = zones.Zones
            .Select(c => new AdminClusterDto(c.Id, c.Name, c.Endpoint, c.ServerFingerprint,
                new AdminClusterProviderSettingsDto(c.OvnUplinkNetwork, c.StoragePool, c.Ipv6Block)))
            .ToList();
        return Task.FromResult(list);
    }
}
