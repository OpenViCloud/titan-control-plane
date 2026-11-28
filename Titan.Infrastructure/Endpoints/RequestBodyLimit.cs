namespace Titan.Infrastructure.Endpoints;

/// Endpoint metadata: raises the request body limit of one route above Kestrel's default (about 28.6 MiB), the others keep
/// the default (titan-ops features/ovc-function/design.md#code-storage). A body over MaxBytes is refused before it is read
/// whole, with 413 and Code.
public sealed record RequestBodyLimit(long MaxBytes, string Code);
