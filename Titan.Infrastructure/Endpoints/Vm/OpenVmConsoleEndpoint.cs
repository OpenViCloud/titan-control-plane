using System.Net.WebSockets;
using System.Security.Claims;
using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Exceptions;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;

namespace Titan.Infrastructure.Endpoints.Vm;

// A console session (titan-ops features/vm/design.md#vm-console): a websocket a browser opens with the one-time ticket in
// its query, since it cannot send a token. Only a bad ticket is refused before the upgrade (a browser cannot read the
// status of a refused handshake); every later refusal closes the websocket with 4000 + the status the same error has on
// a JSON route, and the error code as reason.
// Left out of the OpenAPI spec: a websocket has no JSON body to describe.
public class OpenVmConsoleEndpoint(
    ConsoleTicketStore tickets,
    ITenantRepository tenants,
    GetVmShellTargetQueryHandler target,
    RequestVmConsoleCommandHandler request,
    ShellSessionLimiter limiter,
    IncusShellConnector connector,
    TimeProvider clock,
    ILogger<OpenVmConsoleEndpoint> logger) : EndpointWithoutRequest
{
    public const string TicketInvalidCode = "CONSOLE_TICKET_INVALID";

    public override void Configure()
    {
        Get("v1/tenant/vms/{vmId:guid}/console");
        AllowAnonymous(); // the ticket in the query authenticates the request, below
        Options(x => x.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        bool isUpgrade = HttpContext.WebSockets.IsWebSocketRequest;
        if (!isUpgrade)
        {
            throw new BadRequestException("WEBSOCKET_REQUIRED", "This route opens a console session over a websocket.");
        }
        Guid vmId = Route<Guid>("vmId");
        TakenTicket? taken = await TakeTicketAsync(vmId, ct);
        if (taken == null)
        {
            await JsonErrorWriter.WriteAsync(HttpContext.Response, StatusCodes.Status401Unauthorized, new
            {
                status = StatusCodes.Status401Unauthorized,
                title = "The console ticket is missing, unknown, used or expired; ask for a new one.",
                code = TicketInvalidCode,
            }, ct);
            return;
        }
        ConsoleTicket ticket = taken.Ticket;
        ActAsTheTicketsTaker(ticket);

        using WebSocket customer = await HttpContext.WebSockets.AcceptWebSocketAsync();
        try
        {
            await RunAsync(customer, ticket, taken.TenantStatus, vmId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Console session: the customer left while it opened, vmId {VmId} tenantId {TenantId}",
                vmId, ticket.TenantId);
        }
        catch (Exception e)
        {
            ErrorStatus error = ErrorStatus.For(e);
            if (error.Status == StatusCodes.Status500InternalServerError)
            {
                logger.LogError(e, "Console session failed: vmId {VmId} tenantId {TenantId}", vmId, ticket.TenantId);
            }
            else
            {
                logger.LogInformation("Console session refused: vmId {VmId} tenantId {TenantId} code {Code} detail {Detail}",
                    vmId, ticket.TenantId, error.Code, e.Message);
            }
            await CloseAsync(customer, 4000 + error.Status, error.Code);
        }
    }

    // The ticket's row is gone once read, whatever comes next. A ticket of another VM, or of someone no longer in the
    // tenant, is no ticket.
    private sealed record TakenTicket(ConsoleTicket Ticket, TenantStatus TenantStatus);

    private async Task<TakenTicket?> TakeTicketAsync(Guid vmId, CancellationToken ct)
    {
        string? presented = Query<string>("ticket", isRequired: false);
        if (!ConsoleTicketStore.IsWellFormed(presented))
        {
            return null;
        }
        ConsoleTicket? ticket = await tickets.TakeAsync(presented!, ct);
        if (ticket == null || ticket.VmId != vmId)
        {
            return null;
        }
        (Guid TenantId, TenantStatus Status)? member = await tenants.FindTenantByMemberAsync(ticket.CreatedBy, ct);
        if (member == null || member.Value.TenantId != ticket.TenantId)
        {
            return null;
        }
        return new TakenTicket(ticket, member.Value.Status);
    }

    // The query handlers, the session limit and the activity read the caller from the request, as on every other route.
    private void ActAsTheTicketsTaker(ConsoleTicket ticket)
    {
        HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", ticket.CreatedBy)], "console-ticket"));
        HttpContext.Items["TenantId"] = ticket.TenantId;
    }

    private async Task RunAsync(WebSocket customer, ConsoleTicket ticket, TenantStatus tenantStatus, Guid vmId, CancellationToken ct)
    {
        // The middleware's block on writes did not run on this request: it carries no token.
        if (tenantStatus == TenantStatus.Suspended)
        {
            throw new ForbiddenException("TENANT_SUSPENDED", "Tenant is suspended. Only read operations are allowed.");
        }
        if (tenantStatus == TenantStatus.PendingApproval)
        {
            throw new ForbiddenException("TENANT_PENDING_APPROVAL", "Tenant is awaiting approval. Only read operations are allowed.");
        }
        VmShellTargetQueryResult shell = await target.HandleAsync(new GetVmShellTargetQuery(vmId), ct);
        await using IAsyncDisposable? place = await limiter.TryAcquireAsync(ticket.TenantId, vmId, ct);
        if (place == null)
        {
            throw new BusinessException("VM_SESSION_LIMIT_REACHED",
                $"Your tenant already has {ShellSessionLimiter.MaxSessionsPerTenant} SSH or console sessions open; close one first.");
        }
        await using IncusConsoleSession console = await connector.OpenConsoleAsync(shell.ClusterId, shell.ProjectName, shell.InstanceName, ct);
        await request.HandleAsync(new RequestVmConsoleCommand(vmId), ct);

        long started = clock.GetTimestamp();
        ConsoleRelayResult result = await ConsoleRelay.RunAsync(
            customer, console.Data, console.Control, ConsoleRelayOptions.Default, clock, ct);
        logger.LogInformation(
            "Console session ended: vmId {VmId} tenantId {TenantId} operation {OperationId} ending {Ending} " +
            "durationMs {DurationMs} bytesUp {BytesUp} bytesDown {BytesDown}",
            vmId, ticket.TenantId, console.OperationId, result.Ending, (long)clock.GetElapsedTime(started).TotalMilliseconds,
            result.BytesUp, result.BytesDown);
    }

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

    private static async Task CloseAsync(WebSocket customer, int code, string reason)
    {
        try
        {
            using var giveUp = new CancellationTokenSource(CloseWait);
            await customer.CloseOutputAsync((WebSocketCloseStatus)code, reason, giveUp.Token);
        }
        catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is ObjectDisposedException)
        {
        }
    }
}
