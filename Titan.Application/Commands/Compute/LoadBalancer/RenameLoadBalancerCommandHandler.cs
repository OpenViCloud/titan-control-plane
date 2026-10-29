using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories.Compute;

namespace Titan.Application.Commands.Compute.LoadBalancer;

// Rename in place, Titan-side only: no provider command (titan-ops features/load-balancer/design.md#lb-rename).
public class RenameLoadBalancerCommandHandler(ILoadBalancerRepository repo, ICurrentUser currentUser)
{
    public async Task HandleAsync(RenameLoadBalancerCommand command, CancellationToken ct = default)
    {
        var loadBalancer = await ComputeOwnership.RequireLoadBalancerAsync(repo, currentUser.RequireTenantId(), command.LoadBalancerId, ct);

        // Its own name: no conflict with itself, nothing to write.
        bool sameName = string.Equals(loadBalancer.Name, command.Name, StringComparison.Ordinal);
        if (sameName)
        {
            return;
        }

        if (await repo.ExistsByNameAsync(loadBalancer.TenantId, command.Name, ct))
        {
            throw new ConflictException("LOAD_BALANCER_NAME_TAKEN", $"Load balancer name '{command.Name}' is already taken.");
        }

        loadBalancer.Rename(command.Name);
        await repo.SaveAsync(ct);
    }
}
