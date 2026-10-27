namespace Titan.Application.Abstractions;

// Nothing here is secret: an Incus cluster is reached with Titan's own client identity, not a stored credential
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-incus).
public record AdminClusterDto(
    string ZoneId,
    string Name,
    string Endpoint,
    string ServerFingerprint,
    AdminClusterProviderSettingsDto ProviderSettings);

// Null = not set (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-provider-settings).
public record AdminClusterProviderSettingsDto(string? OvnUplinkNetwork, string? StoragePool, string? Ipv6Block);

public interface IAdminClusterService
{
    /// The configured clusters, in config order.
    Task<List<AdminClusterDto>> ListForAdminAsync(CancellationToken ct);
}
