namespace Titan.Application.Options;

/// The restore drill is off until a deployment turns it on: the control plane that knows the kind ships before the
/// worker that runs it (titan-ops architecture/provider-and-worker.md#contract-changes).
public class RestoreDrillOptions
{
    public const string SectionName = "RestoreDrill";

    public bool Enabled { get; set; }
}
