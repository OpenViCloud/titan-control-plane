namespace Titan.Application.Queries.Catalog.Cluster.Contracts;

// Clusters come from configuration (ClusterOptions), not from a Domain aggregate — hence Cluster/ is a folder
// with no aggregate behind it. The result type is the provider port's ClusterInfo.
public record ListClustersQuery();
