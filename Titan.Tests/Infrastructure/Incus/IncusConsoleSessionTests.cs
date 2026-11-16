using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using Titan.Application.Options;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Incus;

namespace Titan.Tests.Infrastructure.Incus;

// Opening a VM's serial console on the cluster's Incus (titan-ops features/vm/design.md#vm-console): the same client
// certificate and pinned server as every call, the console's two websockets, then one read of the operation, since a
// console Incus cannot attach fails the operation and leaves the sockets open.
public class IncusConsoleSessionTests
{
    private static async Task<(IncusShellConnector Connector, ClusterOptions Configured, string TitanFingerprint)> Connector()
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        var configured = new ClusterOptions();
        return (new IncusShellConnector(configured, new IncusClientFactory(store)), configured, titan.Fingerprint);
    }

    private static async Task<string> ReadTextAsync(WebSocket socket, int length)
    {
        var buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer, read, length - read), CancellationToken.None);
            read += received.Count;
        }
        return Encoding.ASCII.GetString(buffer);
    }

    private static async Task<FakeIncusConsoleServer> ServerAsync(ClusterOptions configured, string titan,
        FakeIncusConsoleServer.Mode mode)
    {
        FakeIncusConsoleServer server = await FakeIncusConsoleServer.StartAsync(titan);
        server.Behaviour = mode;
        configured.Clusters.Add(IncusTestKit.Cluster("local-mac", server.Endpoint, server.Fingerprint));
        return server;
    }

    // No force: a console another tab holds is not taken from it.
    [Fact]
    public async Task OpenConsole_AsksForTheSerialConsole_WithoutForce_AndConnectsItsTwoChannels()
    {
        var (connector, configured, titan) = await Connector();
        await using var server = await ServerAsync(configured, titan, FakeIncusConsoleServer.Mode.Echo);

        await using IncusConsoleSession session = await connector.OpenConsoleAsync("local-mac", "t-abc", "vmxyz", CancellationToken.None);

        string request = Assert.Single(server.ConsoleRequests);
        Assert.StartsWith("/1.0/instances/vmxyz/console?project=t-abc ", request);
        Assert.Contains("\"type\":\"console\"", request);
        Assert.DoesNotContain("force", request);
        Assert.Equal(FakeIncusConsoleServer.Prompt, await ReadTextAsync(session.Data, FakeIncusConsoleServer.Prompt.Length));
        Assert.Equal(WebSocketState.Open, session.Control.State);
    }

    [Fact]
    public async Task AConsoleIncusCannotAttach_IsVmConsoleUnavailable_WithoutWaitingForTheSocketsToClose()
    {
        var (connector, configured, titan) = await Connector();
        await using var server = await ServerAsync(configured, titan, FakeIncusConsoleServer.Mode.CannotAttach);
        var watch = Stopwatch.StartNew();

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            connector.OpenConsoleAsync("local-mac", "t-abc", "vmxyz", CancellationToken.None));

        Assert.Equal("VM_CONSOLE_UNAVAILABLE", refused.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task AConsoleAlreadyOpen_IsVmConsoleBusy()
    {
        var (connector, configured, titan) = await Connector();
        await using var server = await ServerAsync(configured, titan, FakeIncusConsoleServer.Mode.Busy);

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            connector.OpenConsoleAsync("local-mac", "t-abc", "vmxyz", CancellationToken.None));

        Assert.Equal("VM_CONSOLE_BUSY", refused.Code);
    }

    [Fact]
    public async Task AnInstanceNotRunning_IsVmInvalidStatus()
    {
        var (connector, configured, titan) = await Connector();
        await using var server = await ServerAsync(configured, titan, FakeIncusConsoleServer.Mode.NotRunning);

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            connector.OpenConsoleAsync("local-mac", "t-abc", "vmxyz", CancellationToken.None));

        Assert.Equal("VM_INVALID_STATUS", refused.Code);
    }

    // Closing control is how a console ends on Incus (instance_console.go:265-268).
    [Fact]
    public async Task DisposingTheSession_EndsTheConsoleOnIncus()
    {
        var (connector, configured, titan) = await Connector();
        await using var server = await ServerAsync(configured, titan, FakeIncusConsoleServer.Mode.Echo);
        IncusConsoleSession session = await connector.OpenConsoleAsync("local-mac", "t-abc", "vmxyz", CancellationToken.None);

        await session.DisposeAsync();

        Task ended = await Task.WhenAny(server.ConsoleEnded.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(server.ConsoleEnded.Task, ended);
    }
}
