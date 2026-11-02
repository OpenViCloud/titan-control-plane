using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker reports that a VM request's command ended (titan-ops architecture/resource-lifecycle.md#callback-routes): the
// route names the VM and the request, the body says how it ended and what the worker saw. 204 also when the VM is not
// waiting on that request: the worker has nothing to retry.
public class EndVmRequestEndpoint(EndVmRequestCommandHandler handler, IConfiguration config)
    : Endpoint<EndVmRequestBody>
{
    public override void Configure()
    {
        Patch("internal/vms/{vmId:guid}/requests/{requestId:guid}");
        AllowAnonymous();
    }

    public override async Task HandleAsync(EndVmRequestBody body, CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        if (!InternalTokenAuth.Matches(token, config["Internal:Token"]))
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid vmId = Route<Guid>("vmId");
        Guid requestId = Route<Guid>("requestId");
        VmObservationReport? observed = body.Observed == null
            ? null
            : new VmObservationReport(body.Observed.Status, body.Observed.IpAddress, body.Observed.PublicIpv6Address);
        await handler.HandleAsync(
            new EndVmRequestCommand(vmId, requestId, body.Outcome, body.ExternalId, observed, body.Reason, body.Detail), ct);
        await SendNoContentAsync(ct);
    }
}

// The worker's JSON body; EndVmRequestCommand additionally carries the two route ids.
public record EndVmRequestBody(
    string Outcome,
    string? ExternalId = null,
    VmObservedBody? Observed = null,
    string? Reason = null,
    string? Detail = null);

// What the worker read of the instance: the Incus status word for word and its addresses. Shared by the request
// report and the reconciler's observation.
public record VmObservedBody(string Status, string? IpAddress = null, string? PublicIpv6Address = null);
