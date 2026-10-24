namespace Titan.Application.Commands.Platform.BackupTarget.Contracts;

// A failed probe is a result to render, not an HTTP error. Error never contains the secret.
public record BackupTargetTestResult(bool Ok, long? LatencyMs, string? Error);
