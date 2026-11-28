using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Application.Commands.Compute.Function;

// The function-code-cleanup job (titan-ops features/ovc-function/design.md#code-storage): the zip and the image of every
// discarded version, and of every version of a deleted function, then the version's row. The two deletes run outside any
// lock and are idempotent; the row goes last, under the function's lock, so a version whose zip or image could not be
// deleted keeps its row and the next pass tries again. One version's failure does not stop the others.
public class CleanFunctionCodeCommandHandler(
    IFunctionRepository repo,
    IFunctionCodeStore codeStore,
    IFunctionImageRegistry registry,
    ILogger<CleanFunctionCodeCommandHandler> logger)
{
    public const string Job = "function-code-cleanup";

    public async Task HandleAsync(CleanFunctionCodeCommand command, CancellationToken ct = default)
    {
        int cleaned = 0, failed = 0;
        foreach (FunctionAggregate function in await repo.ListWithCodeToCleanAsync(ct))
        {
            foreach (FunctionVersion version in function.VersionsToClean.ToList())
            {
                try
                {
                    await codeStore.DeleteAsync(version.CodeKey, ct);
                    await registry.DeleteFunctionImageAsync(function.TenantId, function.Id, version.ImageDigest, ct);
                }
                catch (FunctionArtifactException e)
                {
                    failed++;
                    logger.LogWarning("Function code cleanup: version {VersionId} of function {FunctionId} kept for the next pass: {Error}",
                        version.Id, function.Id, e.Message);
                    continue;
                }

                FunctionAggregate? locked = await repo.FindByIdForUpdateAsync(function.Id, ct);
                locked?.ForgetCode(version.Id);
                await repo.SaveAsync(ct);
                cleaned++;
            }
        }
        logger.LogInformation("Function code cleanup: {Cleaned} versions removed, {Failed} kept for the next pass", cleaned, failed);
    }
}
