namespace Titan.Application.Commands.Compute.Function.Contracts;

// One secret a version reads, as the upload names it: the tenant's secret by name, and its version number, or null for
// the latest, which the upload pins to a number (titan-ops features/ovc-function/design.md#function-secrets).
public record UploadFunctionSecretRef(string Secret, int? Version);
