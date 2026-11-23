namespace Titan.Application.Options;

/// The restore drill is off until a deployment turns it on: the worker that routes `restore.drill` ships first, then the
/// control plane that asks for it (titan-ops architecture/provider-and-worker.md#contract-changes).
public class RestoreDrillOptions
{
    public const string SectionName = "RestoreDrill";

    public bool Enabled { get; set; }
}
