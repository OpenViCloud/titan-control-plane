using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.VmTemplate;

/// The report that ends a template's sync in one cluster (titan-ops architecture/resource-lifecycle.md#callback-routes):
/// how it ended, the image alias when the worker found it of the cluster's architecture, and for a Failed the fixed
/// reason and the raw detail.
public sealed record VmTemplateOperationEnd(OperationOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static VmTemplateOperationEnd Done(string? externalId) => new(OperationOutcome.Done, externalId, null, null);

    public static VmTemplateOperationEnd Failed(string? externalId, string? reason, string? detail) =>
        new(OperationOutcome.Failed, externalId, reason, detail);
}
