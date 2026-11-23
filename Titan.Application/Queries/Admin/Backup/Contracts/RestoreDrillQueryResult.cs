namespace Titan.Application.Queries.Admin.Backup.Contracts;

/// A cluster's latest restore drill. State is "verified" (the newest ended drill is Done), "notVerified" (it Failed:
/// Reason says why, VerifiedAt is the last Done one if any) or "none" (no drill ended in the last 30 days). VerifiedAt is
/// the moment the drill was asked, the row keeping no finish time. Kind is "vm" or "volume"; Name is its name.
public record RestoreDrillQueryResult(
    string ClusterId,
    string ClusterName,
    string State,
    DateTimeOffset? VerifiedAt,
    string? Kind,
    string? Name,
    string? Reason);
