namespace Titan.Application.Commands.Catalog.VmTemplate.Contracts;

// The report of a template sync's own command in one cluster (titan-ops architecture/resource-lifecycle.md#callback-routes);
// the template, the cluster and the request come from the route. Outcome is "Done" or "Failed"; ExternalId is the image
// alias the worker found.
public record EndTemplateSyncRequestCommand(
    Guid TemplateId,
    string ClusterId,
    Guid RequestId,
    string Outcome,
    string? ExternalId = null,
    string? Reason = null,
    string? Detail = null);
