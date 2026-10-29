// Same namespace as TemplateSyncRunner on purpose (titan-ops architecture/code-structure.md#services-branch).

namespace Titan.Application.Services.Catalog.VmTemplate;

public record TemplateSyncRunResult(TemplateSyncOutcome Outcome, int Total, int Synced, int Failed, int Pending);
