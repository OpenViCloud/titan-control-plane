namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// Customer read. With a cluster: the templates synced in that cluster — what a VM there can be created from. Without
// one: the templates synced in any cluster, for naming the template of a VM that already exists anywhere
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-customer-read).
public record ListTemplatesQuery(string? ClusterId);
