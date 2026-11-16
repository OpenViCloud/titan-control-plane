using FastEndpoints;
using Microsoft.AspNetCore.Http;
using FluentValidation;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Sessions;

namespace Titan.Infrastructure.Endpoints.Vm;

public record CreateVmConsoleTicketRequest(string Ticket);

public class CreateVmConsoleTicketValidator : Validator<CreateVmConsoleTicketRequest>
{
    public CreateVmConsoleTicketValidator()
    {
        RuleFor(x => x.Ticket)
            .NotEmpty()
            .Matches(ConsoleTicketStore.TicketPattern)
            .WithMessage("Ticket must be ovct_ followed by 43 base64url characters.");
    }
}

// The first step of a console session (titan-ops features/vm/design.md#vm-console): the client makes a one-time ticket,
// Titan keeps its hash for 30 s, and the console websocket presents it, since a browser cannot put a token on a
// websocket. Refusals a customer can act on come here, as JSON; the place among the tenant's five sessions is taken when
// the console opens.
public class CreateVmConsoleTicketEndpoint(
    GetVmShellTargetQueryHandler target,
    ICurrentUser currentUser,
    ShellSessionLimiter limiter,
    ConsoleTicketStore tickets) : Endpoint<CreateVmConsoleTicketRequest>
{
    public override void Configure()
    {
        Post("v1/tenant/vms/{vmId:guid}/console-tickets");
        Description(b => b.ClearDefaultProduces(200).Produces(204));
    }

    public override async Task HandleAsync(CreateVmConsoleTicketRequest request, CancellationToken ct)
    {
        Guid vmId = Route<Guid>("vmId");
        await target.HandleAsync(new GetVmShellTargetQuery(vmId), ct);
        Guid tenantId = currentUser.RequireTenantId();
        bool hasRoom = await limiter.HasRoomAsync(tenantId, ct);
        if (!hasRoom)
        {
            throw new BusinessException("VM_SESSION_LIMIT_REACHED",
                $"Your tenant already has {ShellSessionLimiter.MaxSessionsPerTenant} SSH or console sessions open; close one first.");
        }
        await tickets.IssueAsync(tenantId, vmId, currentUser.KeycloakUserId, request.Ticket, ct);
        await SendNoContentAsync(ct);
    }
}
