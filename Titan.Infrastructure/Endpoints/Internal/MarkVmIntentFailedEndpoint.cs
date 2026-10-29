using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Infrastructure.Endpoints.Internal;

// The worker's vm.stop / vm.start handler calls this when its command gave up. No body: the route names the VM and
// the intent id the command carries (titan-ops features/vm/design.md#intent-failed-callback).
public class MarkVmIntentFailedEndpoint(MarkVmIntentFailedCommandHandler handler, IConfiguration config)
    : EndpointWithoutRequest
{
    public override void Configure()
    {
        Patch("internal/vms/{vmId:guid}/intents/{intentId:guid}/failed");
        AllowAnonymous();
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        string? token = HttpContext.Request.Headers["X-Internal-Token"].FirstOrDefault();
        bool callerIsTheWorker = InternalTokenAuth.Matches(token, config["Internal:Token"]);
        if (!callerIsTheWorker)
        {
            await SendUnauthorizedAsync(ct);
            return;
        }

        Guid vmId = Route<Guid>("vmId");
        Guid intentId = Route<Guid>("intentId");
        await handler.HandleAsync(new MarkVmIntentFailedCommand(vmId, intentId), ct);
        await SendNoContentAsync(ct);
    }
}
