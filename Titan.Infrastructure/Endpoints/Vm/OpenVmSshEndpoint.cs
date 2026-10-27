using System.Diagnostics;
using System.Net.WebSockets;
using FastEndpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Sessions;

namespace Titan.Infrastructure.Endpoints.Vm;

// An SSH session: a websocket that carries the bytes of `nc -N 127.0.0.1 22` run in the guest
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session). Every refusal happens before the upgrade, so it is the usual JSON error.
// Left out of the OpenAPI spec: a websocket has no JSON body to describe.
public class OpenVmSshEndpoint(
    GetVmShellTargetQueryHandler target,
    RequestVmSshCommandHandler request,
    ICurrentUser currentUser,
    ShellSessionLimiter limiter,
    IncusShellConnector connector,
    ILogger<OpenVmSshEndpoint> logger) : EndpointWithoutRequest
{
    public override void Configure()
    {
        Get("v1/tenant/vms/{vmId:guid}/ssh");
        Options(x => x.ExcludeFromDescription());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        bool isUpgrade = HttpContext.WebSockets.IsWebSocketRequest;
        if (!isUpgrade)
        {
            throw new BadRequestException("WEBSOCKET_REQUIRED", "This route opens an SSH session over a websocket.");
        }
        Guid vmId = Route<Guid>("vmId");
        VmShellTargetQueryResult shell = await target.HandleAsync(new GetVmShellTargetQuery(vmId), ct);
        Guid tenantId = currentUser.RequireTenantId();
        using IDisposable? place = limiter.TryAcquire(tenantId);
        if (place == null)
        {
            throw new BusinessException("VM_SESSION_LIMIT_REACHED",
                $"Your tenant already has {ShellSessionLimiter.MaxSessionsPerTenant} SSH sessions open; close one first.");
        }
        await request.HandleAsync(new RequestVmSshCommand(vmId), ct);
        await using IncusExecSession exec = await OpenAsync(vmId, shell, ct);

        using WebSocket customer = await HttpContext.WebSockets.AcceptWebSocketAsync();
        var clock = Stopwatch.StartNew();
        ShellRelayResult result = await ShellRelay.RunAsync(
            customer, exec.Stdin, exec.Stdout, exec.Stderr, exec.Control, ShellRelayOptions.Default, ct);
        logger.LogInformation(
            "SSH session ended: vmId {VmId} tenantId {TenantId} operation {OperationId} ending {Ending} " +
            "durationMs {DurationMs} bytesUp {BytesUp} bytesDown {BytesDown} bytesStderr {BytesStderr}",
            vmId, tenantId, exec.OperationId, result.Ending, clock.ElapsedMilliseconds,
            result.BytesUp, result.BytesDown, result.BytesStderr);
    }

    // A cluster failure keeps its code for the customer; the endpoint, fingerprints, project and operation go to the log
    // for the operator, as in the catalog.
    private async Task<IncusExecSession> OpenAsync(Guid vmId, VmShellTargetQueryResult shell, CancellationToken ct)
    {
        try
        {
            return await connector.OpenSshAsync(shell.ClusterId, shell.ProjectName, shell.InstanceName, ct);
        }
        catch (ServiceUnavailableException e)
        {
            logger.LogWarning("SSH session to VM {VmId}: cluster {ClusterId} failed ({Code}): {Detail}", vmId, shell.ClusterId, e.Code, e.Message);
            throw new ServiceUnavailableException(e.Code, $"Cluster '{shell.ClusterId}' is not available ({e.Code}).");
        }
    }
}
