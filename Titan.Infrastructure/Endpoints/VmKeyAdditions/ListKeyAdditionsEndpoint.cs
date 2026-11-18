using FastEndpoints;
using Titan.Application.Queries.Compute.VmKeyAddition;
using Titan.Application.Queries.Compute.VmKeyAddition.Contracts;

namespace Titan.Infrastructure.Endpoints.VmKeyAdditions;

public class ListKeyAdditionsEndpoint(ListVmKeyAdditionsQueryHandler handler)
    : EndpointWithoutRequest<List<VmKeyAdditionQueryResult>>
{
    public override void Configure()
    {
        Get("v1/tenant/vms/{vmId:guid}/key-additions");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var vmId = Route<Guid>("vmId");
        var result = await handler.HandleAsync(new ListVmKeyAdditionsQuery(vmId), ct);
        await SendAsync(result, 200, ct);
    }
}
