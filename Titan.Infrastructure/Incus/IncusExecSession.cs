using System.Net.WebSockets;
using Titan.Domain.Exceptions;
using Titan.Application.Options;

namespace Titan.Infrastructure.Incus;

// The Incus half of an SSH session (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session): `nc -N 127.0.0.1 22` run inside the
// guest by its agent, and the operation's four websockets. -N passes the customer's end of the stream on to sshd; a
// plain nc keeps running after a failed login until sshd's login grace time runs out.
public sealed class IncusShellConnector(ClusterOptions clusters, IncusClientFactory factory)
{
    public static readonly IReadOnlyList<string> SshCommand = ["nc", "-N", "127.0.0.1", "22"];

    // Opening is the exec plus four upgrades; a cluster that stalls one of them must not hold the customer, nor a
    // session place, for as long as the connection stays up.
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public async Task<IncusExecSession> OpenSshAsync(string clusterId, string project, string instance, CancellationToken ct)
    {
        var cluster = clusters.ClusterOf(clusterId);
        IncusClient client = await factory.CreateAsync(cluster.Endpoint, cluster.ServerFingerprint, ct);
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opening.CancelAfter(ConnectTimeout);
        try
        {
            return await OpenAsync(client, project, instance, opening.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new ServiceUnavailableException("CLUSTER_UNREACHABLE",
                $"Cluster '{clusterId}' did not open the session within {ConnectTimeout.TotalSeconds} s.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<IncusExecSession> OpenAsync(IncusClient client, string project, string instance, CancellationToken ct)
    {
        IncusExecOperation operation = await client.StartExecAsync(project, instance, SshCommand, ct);
        var sockets = new Dictionary<string, WebSocket>();
        try
        {
            foreach (string fd in new[] { "0", "1", "2", "control" })
            {
                sockets[fd] = await ConnectAsync(client, operation, fd, ct);
            }
        }
        catch
        {
            foreach (WebSocket socket in sockets.Values)
            {
                socket.Dispose();
            }
            throw;
        }
        return new IncusExecSession(client, operation.Id, sockets["0"], sockets["1"], sockets["2"], sockets["control"]);
    }

    // A VM whose agent has not started yet takes the exec, then refuses the websocket with a 500; the reason is only on
    // the operation.
    private static async Task<WebSocket> ConnectAsync(IncusClient client, IncusExecOperation operation, string fd, CancellationToken ct)
    {
        bool hasSecret = operation.Secrets.TryGetValue(fd, out string? secret);
        if (!hasSecret)
        {
            throw new ServiceUnavailableException("CLUSTER_ERROR", $"Incus exec {operation.Id} has no websocket '{fd}'.");
        }
        try
        {
            return await client.ConnectOperationAsync(operation.Id, secret!, ct);
        }
        catch (WebSocketException e)
        {
            string reason = await client.GetOperationErrorAsync(operation.Id, ct);
            bool agentDown = reason.Contains("agent", StringComparison.OrdinalIgnoreCase);
            if (agentDown)
            {
                throw new BusinessException("VM_AGENT_NOT_READY", "The VM is still starting; try again in a minute.");
            }
            throw new ServiceUnavailableException("CLUSTER_ERROR", $"Incus refused websocket '{fd}' of exec {operation.Id}: {e.Message} {reason}");
        }
    }
}

public sealed class IncusExecSession(
    IncusClient client, string operationId, WebSocket stdin, WebSocket stdout, WebSocket stderr, WebSocket control)
    : IAsyncDisposable
{
    public string OperationId { get; } = operationId;
    public WebSocket Stdin { get; } = stdin;
    public WebSocket Stdout { get; } = stdout;
    public WebSocket Stderr { get; } = stderr;
    public WebSocket Control { get; } = control;

    public ValueTask DisposeAsync()
    {
        Stdin.Dispose();
        Stdout.Dispose();
        Stderr.Dispose();
        Control.Dispose();
        client.Dispose();
        return ValueTask.CompletedTask;
    }
}
