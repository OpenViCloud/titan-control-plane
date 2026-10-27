namespace Titan.Application.Queries.Catalog.VmTemplate.Contracts;

// The template reconciler of one cluster asks which image aliases to observe on that cluster.
public record ListReconcilableTemplatesQuery(string ClusterId);
