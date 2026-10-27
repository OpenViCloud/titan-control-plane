using Titan.Domain.Exceptions;

namespace Titan.Application.Options;

// A cluster: where a customer places resources, and the Incus cluster Titan reaches there. All of it is config
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-from-config); Titan authenticates with its own client identity, so
// nothing here is secret.
public class ZoneConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string ServerFingerprint { get; set; } = "";

    // Provider settings (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-provider-settings); null = not set.
    public string? OvnUplinkNetwork { get; set; }
    public string? StoragePool { get; set; }
    public string? Ipv6Block { get; set; }

    public const int MaxProviderNameLength = 64;

    public static string NormalizeEndpoint(string? endpoint)
    {
        string trimmed = (endpoint ?? "").Trim().TrimEnd('/');
        bool isAbsolute = Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri);
        bool isHttpsWithAHost = isAbsolute && uri!.Scheme == Uri.UriSchemeHttps && uri.Host != "";
        if (!isHttpsWithAHost)
        {
            throw new ArgumentException("endpoint must be an https URL");
        }
        return trimmed;
    }

    // Accepts what `incus remote add` and openssl print (colons, upper case); keeps what the handshake compares.
    public static string NormalizeFingerprint(string? serverFingerprint)
    {
        string hex = (serverFingerprint ?? "").Trim().Replace(":", "").ToLowerInvariant();
        bool isSha256Hex = hex.Length == 64 && hex.All(Uri.IsHexDigit);
        if (!isSha256Hex)
        {
            throw new ArgumentException("serverFingerprint must be a SHA-256 fingerprint (64 hex digits)");
        }
        return hex;
    }

    // The address plan numbers the 256 /64s of a /56; a block written from a host inside it would shift every /64.
    public static bool IsIpv6Block(string block)
    {
        bool parsed = System.Net.IPNetwork.TryParse(block, out var network);
        if (!parsed)
        {
            return false;
        }
        bool isIpv6 = network.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        return isIpv6 && network.PrefixLength == 56;
    }
}

public class ZoneOptions
{
    public const string SectionName = "Clusters";

    public List<ZoneConfig> Zones { get; set; } = [];

    /// Checks and normalizes what the configuration holds; a value no cluster can be reached with throws, so the
    /// control plane does not start.
    public static ZoneOptions FromConfiguration(IEnumerable<ZoneConfig>? configured)
    {
        var clusters = new List<ZoneConfig>();
        int index = 0;
        foreach (var cluster in configured ?? [])
        {
            clusters.Add(Checked(cluster, $"{SectionName}:{index}"));
            index++;
        }
        var repeated = clusters.GroupBy(c => c.Id).FirstOrDefault(g => g.Count() > 1);
        if (repeated != null)
        {
            throw new InvalidOperationException($"{SectionName}: cluster '{repeated.Key}' is configured twice.");
        }
        return new ZoneOptions { Zones = clusters };
    }

    // A zone the customer picks must be one Titan has: an unknown id would fall through the cluster resolver to
    // the config section's cluster (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#zone-is-a-real-choice).
    public void Require(string zoneId)
    {
        bool zoneExists = Zones.Any(z => z.Id == zoneId);
        if (!zoneExists)
        {
            throw new NotFoundException("ZONE_NOT_FOUND", $"Zone '{zoneId}' not found.");
        }
    }

    public ZoneConfig ClusterOf(string zoneId)
    {
        ZoneConfig? cluster = Zones.FirstOrDefault(z => z.Id == zoneId);
        if (cluster == null)
        {
            throw new NotFoundException("CLUSTER_NOT_FOUND", $"Zone '{zoneId}' has no cluster.");
        }
        return cluster;
    }

    private static ZoneConfig Checked(ZoneConfig cluster, string path)
    {
        if (string.IsNullOrWhiteSpace(cluster.Id))
        {
            throw new InvalidOperationException($"{path}:Id must not be blank.");
        }
        string id = cluster.Id.Trim();
        return new ZoneConfig
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(cluster.Name) ? id : cluster.Name.Trim(),
            Endpoint = Field(path, nameof(ZoneConfig.Endpoint), () => ZoneConfig.NormalizeEndpoint(cluster.Endpoint)),
            ServerFingerprint = Field(path, nameof(ZoneConfig.ServerFingerprint),
                () => ZoneConfig.NormalizeFingerprint(cluster.ServerFingerprint)),
            OvnUplinkNetwork = ProviderName(path, nameof(ZoneConfig.OvnUplinkNetwork), cluster.OvnUplinkNetwork),
            StoragePool = ProviderName(path, nameof(ZoneConfig.StoragePool), cluster.StoragePool),
            Ipv6Block = Ipv6Block(path, cluster.Ipv6Block),
        };
    }

    private static string Field(string path, string field, Func<string> normalize)
    {
        try
        {
            return normalize();
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException($"{path}:{field}: {e.Message}.");
        }
    }

    // A blank value is "not set", not an empty name sent to Incus.
    private static string? ProviderName(string path, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string name = value.Trim();
        if (name.Length > ZoneConfig.MaxProviderNameLength)
        {
            throw new InvalidOperationException($"{path}:{field} must be at most {ZoneConfig.MaxProviderNameLength} characters.");
        }
        return name;
    }

    private static string? Ipv6Block(string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        string block = value.Trim();
        if (!ZoneConfig.IsIpv6Block(block))
        {
            throw new InvalidOperationException(
                $"{path}:{nameof(ZoneConfig.Ipv6Block)} must be an IPv6 /56, written as its first address.");
        }
        return System.Net.IPNetwork.Parse(block).ToString();
    }
}
