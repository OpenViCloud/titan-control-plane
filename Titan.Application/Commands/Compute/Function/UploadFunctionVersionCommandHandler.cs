using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Application.Exceptions;
using Titan.Application.Services.Compute;
using Titan.Application.Services.Compute.Function;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Aggregates.FunctionSecret;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;
using FunctionSecretAggregate = Titan.Domain.Aggregates.FunctionSecret.FunctionSecret;
using FunctionRuntimeAggregate = Titan.Domain.Aggregates.FunctionRuntime.FunctionRuntime;

namespace Titan.Application.Commands.Compute.Function;

// One upload (titan-ops features/ovc-function/design.md#code-storage): the zip is checked and packed, written to the
// function bucket, and pushed as a function image before the version row exists, so a version always names code that
// is there. The function's row is locked only for the last step: the bucket and the registry are slow, and a lock held
// across them would hold back every report and observation of the function. A failure after the bucket write leaves
// objects no row names (a risk accepted, titan-ops features/ovc-function/design.md#debts).
public class UploadFunctionVersionCommandHandler(
    IFunctionRepository repo,
    IFunctionSecretRepository secrets,
    IFunctionRuntimeRepository runtimes,
    IFunctionCodeInspector inspector,
    IFunctionCodeStore codeStore,
    IFunctionImageRegistry registry,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public async Task<UploadFunctionVersionCommandResult> HandleAsync(UploadFunctionVersionCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        FunctionAggregate function = await ComputeOwnership.RequireFunctionAsync(repo, tenantId, command.FunctionId, ct);
        function.EnsureTakesVersions();

        // The catalog is a handful of seeded rows.
        FunctionRuntimeAggregate? runtime = (await runtimes.ListAllAsync(ct)).FirstOrDefault(r => r.Name == command.Runtime);
        DateOnly today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        FunctionCodeLanguage? language = null;
        if (runtime != null && runtime.TakesNewVersionsOn(today))
        {
            language = FunctionRuntimeLanguage.Of(runtime.Name);
        }
        if (runtime == null || language == null)
        {
            throw new BusinessException("FUNCTION_RUNTIME_NOT_USABLE",
                $"Runtime '{command.Runtime}' is not in the catalog, or no longer takes new versions.");
        }

        // Go's bootstrap registers its own handler; Python and Node.js name the module and the function to call.
        string? handler = null;
        if (language != FunctionCodeLanguage.Go)
        {
            if (string.IsNullOrWhiteSpace(command.Handler))
            {
                throw new BadRequestException("VALIDATION_ERROR", $"Runtime '{runtime.Name}' needs a handler, \"module.function\".");
            }
            handler = command.Handler;
        }

        // Every secret is resolved before the code is packed, stored or pushed: a version names only secret versions that
        // exist.
        List<FunctionSecretRef> secretRefs = await ResolveSecretsAsync(tenantId, command, ct);

        Guid versionId = Guid.NewGuid();
        string codeKey = FunctionAggregate.CodeKeyFor(tenantId, function.Id, versionId);
        FunctionCodeLayer layer = await inspector.PackAsync(command.ZipPath, language.Value, handler, command.WorkDir, ct);
        string digest;
        try
        {
            await codeStore.PutAsync(codeKey, command.ZipPath, ct);
            await registry.EnsureRuntimeCopyAsync(runtime.Name, runtime.Image, ct);
            digest = await registry.PushFunctionImageAsync(runtime.Name, runtime.Image, tenantId, function.Id, versionId, layer, ct);
        }
        catch (FunctionArtifactException e)
        {
            throw new ServiceUnavailableException("FUNCTION_ARTIFACT_UNAVAILABLE", e.Message);
        }
        string image = registry.PullReference(tenantId, function.Id, digest);

        // Read again under the lock: a delete or another upload may have come meanwhile, and AddVersion decides on what
        // the row holds now.
        FunctionAggregate locked = await ComputeOwnership.RequireFunctionForUpdateAsync(repo, tenantId, function.Id, ct);
        FunctionVersion version = locked.AddVersion(new FunctionVersionSpec(
            versionId, runtime.Id, runtime.Name, runtime.Image, handler, command.MemoryMiB, command.TimeoutSeconds,
            codeKey, layer.ZipSha256, layer.ZipBytes, layer.UnpackedBytes, layer.FileCount, digest, image,
            command.Env, secretRefs, command.Schedule));
        await repo.SaveAsync(ct);
        return new UploadFunctionVersionCommandResult(version.Id);
    }

    // Each secret by name in the tenant, "latest" pinned to the newest number now (titan-ops features/ovc-function/design.md#function-secrets).
    // A name the tenant has no secret of, or a number no longer kept, is 422 FUNCTION_SECRET_NOT_FOUND; the variables
    // and secrets together over the limit, counted with each secret version's length, is 400.
    private async Task<List<FunctionSecretRef>> ResolveSecretsAsync(Guid tenantId, UploadFunctionVersionCommand command, CancellationToken ct)
    {
        var refs = new List<FunctionSecretRef>();
        var weights = new List<(string EnvName, int ValueBytes)>();
        foreach ((string envName, UploadFunctionSecretRef asked) in command.Secrets.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            FunctionSecretAggregate? secret = await secrets.FindByNameAsync(tenantId, asked.Secret, ct);
            int number = asked.Version ?? secret?.LatestVersionNumber ?? 0;
            FunctionSecretVersion? secretVersion = secret?.FindVersion(number);
            if (secret == null || secretVersion == null)
            {
                string which = asked.Version == null ? "" : $" version {asked.Version}";
                throw new BusinessException("FUNCTION_SECRET_NOT_FOUND",
                    $"Function secret '{asked.Secret}'{which}, which {envName} reads, does not exist.");
            }
            refs.Add(FunctionSecretRef.Of(envName, secret.Id, secret.Name, secretVersion.Number));
            weights.Add((envName, secretVersion.ValueBytes));
        }
        if (!FunctionAggregate.IsAConfigSize(FunctionAggregate.ConfigBytes(command.Env, weights)))
        {
            throw new BadRequestException("VALIDATION_ERROR",
                $"The variables and secrets of a version are at most {FunctionAggregate.MaxConfigBytes} bytes together.");
        }
        return refs;
    }
}
