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
using Titan.Application.Exceptions;

namespace Titan.Infrastructure.Endpoints.Vm;

// An SSH session: a websocket that carries the bytes of `nc -N 127.0.0.1 22` run in the guest
// (titan-ops features/vm/design.md#ssh-session). Every refusal happens before the upgrade, so it is the usual JSON error.
// The session opens, and is recorded, once the guest's SSH server has spoken, or once BannerWait is over with the
// command still running.
// Left out of the OpenAPI spec: a websocket has no JSON body to describe.
public class OpenVmSshEndpoint(
    GetVmShellTargetQueryHandler target,
    RequestVmSshCommandHandler request,
    ICurrentUser currentUser,
    ShellSessionLimiter limiter,
    IncusShellConnector connector,
    TimeProvider clock,
    ILogger<OpenVmSshEndpoint> logger) : EndpointWithoutRequest
{
    // How long an SSH server has to send its banner before the session opens anyway.
    public static readonly TimeSpan BannerWait = TimeSpan.FromSeconds(5);

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
        await using IAsyncDisposable? place = await limiter.TryAcquireAsync(tenantId, vmId, ct);
        if (place == null)
        {
            throw new BusinessException("VM_SESSION_LIMIT_REACHED",
                $"Your tenant already has {ShellSessionLimiter.MaxSessionsPerTenant} SSH sessions open; close one first.");
        }
        await using IncusExecSession exec = await OpenAsync(vmId, shell, ct);
        ShellFirstOutput stdout = ShellFirstOutput.Start(exec.Stdout);
        bool nothingListening = await stdout.EndedWithoutOutputWithinAsync(BannerWait, clock);
        if (nothingListening)
        {
            int? refusedExitCode = await exec.ReadExitCodeAsync(ct);
            logger.LogInformation(
                "SSH session refused: vmId {VmId} tenantId {TenantId} operation {OperationId} nothing listening on port 22 exitCode {ExitCode}",
                vmId, tenantId, exec.OperationId, refusedExitCode);
            throw new BusinessException("VM_SSH_NOT_READY",
                "The VM is running but its SSH server is not listening yet; try again in a minute.");
        }
        await request.HandleAsync(new RequestVmSshCommand(vmId), ct);

        using WebSocket customer = await HttpContext.WebSockets.AcceptWebSocketAsync();
        long started = clock.GetTimestamp();
        ShellRelayResult result = await ShellRelay.RunAsync(
            customer, exec.Stdin, stdout, exec.Stderr, exec.Control, ShellRelayOptions.Default, clock, ct);
        int? exitCode = await exec.ReadExitCodeAsync(CancellationToken.None);
        logger.LogInformation(
            "SSH session ended: vmId {VmId} tenantId {TenantId} operation {OperationId} ending {Ending} exitCode {ExitCode} " +
            "durationMs {DurationMs} bytesUp {BytesUp} bytesDown {BytesDown} bytesStderr {BytesStderr}",
            vmId, tenantId, exec.OperationId, result.Ending, exitCode, (long)clock.GetElapsedTime(started).TotalMilliseconds,
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
