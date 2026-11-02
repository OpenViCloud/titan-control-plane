namespace Titan.Application.Options;

/// <summary>When a waiting request counts as stuck (titan-ops architecture/resource-lifecycle.md#lost-and-stale).</summary>
public class RequestStuckOptions
{
    public const string SectionName = "RequestStuck";

    /// <summary>
    /// A request waiting longer than this is listed by GET /v1/admin/requests/stuck and turns /healthz/reconcilers.
    /// 15: a chosen threshold, not a retry budget. One run of a VM command may itself take that long, so a row still open
    /// past it can be a slow worker as well as an absent one.
    /// </summary>
    public int ThresholdMinutes { get; set; } = 15;
}
