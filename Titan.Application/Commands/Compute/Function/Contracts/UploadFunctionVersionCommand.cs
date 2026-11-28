namespace Titan.Application.Commands.Compute.Function.Contracts;

// One upload (titan-ops features/ovc-function/design.md#code-storage): the function from the route, the config part of
// the form, and the code part already on disk at ZipPath. WorkDir is the upload's own temporary directory, where the
// code layer is written; the endpoint removes both.
public record UploadFunctionVersionCommand(
    Guid FunctionId,
    string Runtime,
    string? Handler,
    int MemoryMiB,
    int TimeoutSeconds,
    string ZipPath,
    string WorkDir);
