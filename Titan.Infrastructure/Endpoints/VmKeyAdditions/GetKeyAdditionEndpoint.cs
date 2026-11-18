using FastEndpoints;
using Titan.Application.Queries.Compute.VmKeyAddition;
using Titan.Application.Queries.Compute.VmKeyAddition.Contracts;

namespace Titan.Infrastructure.Endpoints.VmKeyAdditions;

public class GetKeyAdditionEndpoint(GetVmKeyAdditionQueryHandler handler)
    : EndpointWithoutRequest<VmKeyAdditionQueryResult>
{
    public override void Configure()
    {
        Get("v1/tenant/vms/{vmId:guid}/key-additions/{keyAdditionId:guid}");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        var keyAdditionId = Route<Guid>("keyAdditionId");
        var result = await handler.HandleAsync(new GetVmKeyAdditionQuery(vmId, keyAdditionId), ct);
        await SendAsync(result, 200, ct);
    }
}
