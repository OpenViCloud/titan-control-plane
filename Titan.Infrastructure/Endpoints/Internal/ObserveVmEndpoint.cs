using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The reconciler's observation of one VM (titan-ops architecture/resource-lifecycle.md#callback-routes): whether Incus has
// the instance and what it read. Writes current, ends no request.
public class ObserveVmEndpoint(ObserveVmCommandHandler handler, IConfiguration config)
    : Endpoint<ObserveVmBody>
{
    public override void Configure()
    {
        Patch("internal/vms/{vmId:guid}/observation");
        AllowAnonymous();
    }

    public override async Task HandleAsync(ObserveVmBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid vmId = Route<Guid>("vmId");
        VmObservationReport? observed = body.Observed == null
            ? null
            : new VmObservationReport(body.Observed.Status, body.Observed.IpAddress, body.Observed.PublicIpv6Address);
        await handler.HandleAsync(new ObserveVmCommand(vmId, body.Exists, observed), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; ObserveVmCommand additionally carries the route id.
public record ObserveVmBody(bool Exists, VmObservedBody? Observed = null);
