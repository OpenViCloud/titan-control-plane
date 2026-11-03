using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.VmTemplate;

/// The report that ends a template's sync in one cluster (titan-ops architecture/resource-lifecycle.md#callback-routes):
/// how it ended, the image alias when the worker found it of the cluster's architecture, and for a Failed the fixed
/// reason and the raw detail.
public sealed record VmTemplateRequestEnd(RequestOutcome Outcome, string? ExternalId, string? Reason, string? Detail)
{
    public static VmTemplateRequestEnd Done(string? externalId) => new(RequestOutcome.Done, externalId, null, null);

    public static VmTemplateRequestEnd Failed(string? externalId, string? reason, string? detail) =>
        new(RequestOutcome.Failed, externalId, reason, detail);
}
