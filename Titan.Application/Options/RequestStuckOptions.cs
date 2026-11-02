namespace Titan.Application.Options;

/// <summary>When a waiting request counts as stuck (titan-ops architecture/resource-lifecycle.md#lost-and-stale).</summary>
public class RequestStuckOptions
{
    public const string SectionName = "RequestStuck";

    /// <summary>
    /// A request waiting longer than this is listed by GET /v1/admin/requests/stuck and turns /healthz/reconcilers.
    /// 15: a command gets at most 5 leases of 60 s with backoff up to 2 min between them, so a request still waiting
    /// after 15 minutes has outlived every retry.
    /// </summary>
    public int ThresholdMinutes { get; set; } = 15;
}
