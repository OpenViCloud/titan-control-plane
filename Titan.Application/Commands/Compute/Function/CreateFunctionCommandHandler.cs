using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Compute;
using Titan.Application.Services.Platform.Tenant;
using Titan.Domain.Repositories;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Application.Commands.Compute.Function;

// Titan-side only: nothing reaches the function cluster before the first deploy
// (titan-ops features/ovc-function/design.md#function-lifecycle).
public class CreateFunctionCommandHandler(IFunctionRepository repo, FunctionClusterOptions clusters, ICurrentUser currentUser)
{
    public async Task<CreateFunctionCommandResult> HandleAsync(CreateFunctionCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        ComputeInput.EnsureFunctionName(command.Name);
        // A function runs only on a function cluster; an Incus cluster's id is as unknown here as any other.
        string clusterId = clusters.ClusterOf(command.ClusterId).Id;

        int count = await repo.CountActiveByTenantAsync(tenantId, ct);
        TenantQuotaGate.EnsureFunctionRoom(count);
        FunctionAggregate function = FunctionAggregate.Create(tenantId, clusterId, command.Name);
        repo.Add(function);
        await repo.SaveAsync(ct);
        return new CreateFunctionCommandResult(function.Id);
    }
}
