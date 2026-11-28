using Titan.Domain.Exceptions;

namespace Titan.Application.Options;

// A function cluster: a Kubernetes cluster where customers' functions run (titan-ops features/ovc-function/design.md#function-cluster-ops).
// Its worker runs inside it and reaches the control plane, never the other way round, so there is no endpoint and no
// fingerprint: only the id its commands carry and the name an admin reads.
public class FunctionClusterConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

// A list of its own beside ClusterOptions: the Incus clusters, and GET /internal/clusters that the Incus worker
// inventories, stay as they are.
public class FunctionClusterOptions
{
    public const string SectionName = "FunctionClusters";

    public List<FunctionClusterConfig> Clusters { get; set; } = [];

    /// A blank id, an id given twice, the id of an Incus cluster, or a second function cluster throws, so the control
    /// plane does not start: a command's cluster_id must name the one cluster whose worker runs it, and a worker leases
    /// by kind, not by cluster, so it would lease (and fail for good) another cluster's command.
    public static FunctionClusterOptions FromConfiguration(IEnumerable<FunctionClusterConfig>? configured, ClusterOptions incus)
    {
        var clusters = new List<FunctionClusterConfig>();
        int index = 0;
        foreach (FunctionClusterConfig cluster in configured ?? [])
        {
            string path = $"{SectionName}:{index}:{nameof(FunctionClusterConfig.Id)}";
            if (string.IsNullOrWhiteSpace(cluster.Id))
            {
                throw new InvalidOperationException($"{path} must not be blank.");
            }
            string id = cluster.Id.Trim();
            if (clusters.Any(c => c.Id == id))
            {
                throw new InvalidOperationException($"{path}: function cluster '{id}' is configured twice.");
            }
            if (incus.Clusters.Any(c => c.Id == id))
            {
                throw new InvalidOperationException(
                    $"{path}: '{id}' is the id of a cluster in {ClusterOptions.SectionName}; a function cluster needs an id of its own.");
            }
            clusters.Add(new FunctionClusterConfig
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(cluster.Name) ? id : cluster.Name.Trim(),
            });
            index++;
        }
        if (clusters.Count > 1)
        {
            throw new InvalidOperationException(
                $"{SectionName}:1:{nameof(FunctionClusterConfig.Id)}: one function cluster at most, until a lease names its cluster.");
        }
        return new FunctionClusterOptions { Clusters = clusters };
    }

    public FunctionClusterConfig ClusterOf(string clusterId)
    {
        FunctionClusterConfig? cluster = Clusters.FirstOrDefault(c => c.Id == clusterId);
        if (cluster == null)
        {
            throw new NotFoundException("CLUSTER_NOT_FOUND", $"Function cluster '{clusterId}' is not configured.");
        }
        return cluster;
    }
}
