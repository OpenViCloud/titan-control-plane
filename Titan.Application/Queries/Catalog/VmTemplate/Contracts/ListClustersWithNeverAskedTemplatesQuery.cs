namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// The control plane's own read (TemplateSyncNeverAskedService): the configured clusters some template was never asked
// into. No parameters.
public record ListClustersWithNeverAskedTemplatesQuery();
