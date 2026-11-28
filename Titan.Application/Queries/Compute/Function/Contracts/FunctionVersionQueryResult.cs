using Titan.Domain.Aggregates.Function;

namespace Titan.Application.Queries.Compute.Function.Contracts;

// A kept version, as the customer sees it: the runtime by name, not the image, and the zip's digest so a client can
// tell which code it uploaded (titan-ops features/ovc-function/design.md#function-versions).
public record FunctionVersionQueryResult(
    Guid Id,
    int Number,
    string Runtime,
    string? Handler,
    int MemoryMiB,
    int TimeoutSeconds,
    string CodeSha256,
    long CodeBytes,
    DateTimeOffset CreatedAt)
{
    public static FunctionVersionQueryResult From(FunctionVersion v) =>
        new(v.Id, v.Number, v.RuntimeName, v.Handler, v.MemoryMiB, v.TimeoutSeconds, v.CodeSha256, v.CodeBytes, v.CreatedAt);
}
