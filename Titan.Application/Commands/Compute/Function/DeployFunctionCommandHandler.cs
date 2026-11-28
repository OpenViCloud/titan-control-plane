using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Application.Exceptions;
using Titan.Application.Services.Compute;
using Titan.Application.Services.Compute.FunctionSecret;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;
using FunctionRuntimeAggregate = Titan.Domain.Aggregates.FunctionRuntime.FunctionRuntime;

namespace Titan.Application.Commands.Compute.Function;

// Returns nothing (titan-ops architecture/http-contract.md#async-actions-return-202): the worker deploys later, and the
// customer reads the function back.
public class DeployFunctionCommandHandler(
    IFunctionRepository repo,
    IFunctionSecretRepository secrets,
    IFunctionRuntimeRepository runtimes,
    IFunctionImageRegistry registry,
    ICurrentUser currentUser,
    IProviderCommandQueue commands)
{
    public async Task HandleAsync(DeployFunctionCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        // Locked, so two deploys take turns and the second sees the first waiting
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        FunctionAggregate function = await ComputeOwnership.RequireFunctionForUpdateAsync(repo, tenantId, command.FunctionId, ct);
        FunctionVersion version = function.GetVersion(command.VersionId);

        // The aggregate refuses a repeat, so the handler recognises it first
        // (titan-ops architecture/intent-and-observation.md#domain-refuses-a-repeat): answered like the first request,
        // nothing written, nothing sent.
        if (function.IsDeployWaitingOrDone(version.Id))
        {
            return;
        }

        // The deploy gate (titan-ops features/ovc-function/design.md#runtimes): every Ready node holds the runtime, and
        // the registry holds the function image the nodes will pull. Not the runtime's support date: a version packed
        // before it may be deployed again after it.
        FunctionRuntimeAggregate? runtime = (await runtimes.ListAllAsync(ct)).FirstOrDefault(r => r.Id == version.RuntimeId);
        bool runtimeThere = runtime != null && runtime.IsAvailableIn(function.ClusterId);
        bool imageThere = false;
        if (runtimeThere)
        {
            try
            {
                imageThere = await registry.HasFunctionImageAsync(function.TenantId, function.Id, version.ImageDigest, ct);
            }
            catch (FunctionArtifactException e)
            {
                throw new ServiceUnavailableException("FUNCTION_ARTIFACT_UNAVAILABLE", e.Message);
            }
        }
        if (!runtimeThere || !imageThere)
        {
            throw new BusinessException("FUNCTION_RUNTIME_NOT_USABLE",
                $"Version {version.Number} cannot be deployed: its runtime '{version.RuntimeName}' is not on the function " +
                "cluster, or its image is not in the registry.");
        }

        // Every secret version the version pins must still be there: the worker reads their values during the deploy
        // (titan-ops features/ovc-function/design.md#function-secrets). Nothing is sent otherwise.
        await FunctionSecretRefs.RequireKeptAsync(secrets, function.TenantId, version.SecretRefs, ct);

        Guid requestId = function.Deploy(version.Id);
        await commands.EnqueueFunctionDeployAsync(function, version, requestId, ct);
        await repo.SaveAsync(ct);
    }
}
