using System.Net.WebSockets;
using System.Text;
using Titan.Application.Options;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Incus;

// The relay between the customer's websocket and the exec's (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session), against an
// Incus that ends a command the way Incus does: an empty message, then sockets dropped without a close handshake.
public class ShellRelayTests
{
    private static readonly ShellRelayOptions Fast = new(
        IdleTimeout: TimeSpan.FromSeconds(30), DrainTimeout: TimeSpan.FromMilliseconds(300), SignalWait: TimeSpan.FromSeconds(2));

    private sealed record Rig(FakeIncusExecServer Incus, IncusExecSession Session, WebSocket Server, WebSocket Customer) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Server.Dispose();
            Customer.Dispose();
            await Incus.DisposeAsync();
        }
    }

    private static async Task<Rig> StartAsync(FakeIncusExecServer.Mode mode)
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        var configured = new ClusterOptions();
        FakeIncusExecServer incus = await FakeIncusExecServer.StartAsync(titan.Fingerprint);
        incus.Behaviour = mode;
        configured.Clusters.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));
        IncusExecSession session = await new IncusShellConnector(configured, new IncusClientFactory(store))
            .OpenSshAsync("local-mac", "t-abc", "vmxyz", CancellationToken.None);
        (WebSocket server, WebSocket customer) = InMemoryWebSocket.Pair();
        return new Rig(incus, session, server, customer);
    }

    private static Task<ShellRelayResult> RelayAsync(Rig rig, ShellRelayOptions options) =>
        RelayAsync(rig, options, CancellationToken.None);

    private static Task<ShellRelayResult> RelayAsync(Rig rig, ShellRelayOptions options, CancellationToken customerGone) =>
        ShellRelay.RunAsync(rig.Server, rig.Session.Stdin, rig.Session.Stdout, rig.Session.Stderr, rig.Session.Control,
            options, customerGone);

    private static async Task<string> ReadAsync(WebSocket socket, int length)
    {
        var buffer = new byte[length];
        int read = 0;
        while (read < length)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, read, length - read), CancellationToken.None);
            read += received.Count;
        }
        return Encoding.ASCII.GetString(buffer);
    }

    private static async Task<WebSocketCloseStatus?> ReadUntilClosedAsync(WebSocket socket)
    {
        var buffer = new byte[1024];
        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return received.CloseStatus;
            }
        }
    }

    [Fact]
    public async Task Bytes_FlowBothWays_AndTheCustomersCloseEndsTheCommand_WithoutASignal()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Echo);
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast);

        Assert.Equal(FakeIncusExecServer.Banner, await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length));
        await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("hello"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("hello", await ReadAsync(rig.Customer, 5));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.CustomerClosed, result.Ending);
        Assert.Equal(5, result.BytesUp);
        Assert.Equal(FakeIncusExecServer.Banner.Length + 5, result.BytesDown);
        Assert.Empty(rig.Incus.ControlMessages);
    }

    // Incus writes a large message as one frame without FIN and an empty final frame: the empty frame finishes the
    // message, it is not the end of the command.
    [Fact]
    public async Task AFragmentedBurst_IsCarriedWhole_AndTheSessionGoesOn()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.FragmentedBurst);
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length + FakeIncusExecServer.BurstBytes);
        await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("x"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("x", await ReadAsync(rig.Customer, 1));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.CustomerClosed, result.Ending);
        Assert.Equal(FakeIncusExecServer.Banner.Length + FakeIncusExecServer.BurstBytes + 1, result.BytesDown);
    }

    // ssh kills its ProxyCommand on exit, so most sessions end with the customer's connection dropped and the request
    // aborted: the command still gets its end of input, and the session ends as the customer's.
    [Fact]
    public async Task ACustomerWhoDrops_StillClosesTheCommandsInput()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Echo);
        using var requestAborted = new CancellationTokenSource();
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast, requestAborted.Token);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        rig.Customer.Abort();
        requestAborted.Cancel();

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.CustomerClosed, result.Ending);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, Assert.Single(rig.Incus.StdinCloseStatuses));
        Assert.Empty(rig.Incus.ControlMessages);
    }

    // The same drop with a command that keeps running: the drain and the signal still happen; the request's abort must
    // not cut them short.
    [Fact]
    public async Task ACustomerWhoDrops_StillGetsALingeringCommandSignalled()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.IgnoresEof);
        using var requestAborted = new CancellationTokenSource();
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast, requestAborted.Token);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        rig.Customer.Abort();
        requestAborted.Cancel();

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.CustomerClosedCommandSignalled, result.Ending);
        Assert.Equal("{\"command\":\"signal\",\"signal\":15}", Assert.Single(rig.Incus.ControlMessages));
    }

    // A customer who stops reading (ssh suspended) leaves the relay's send to it waiting: ending the session must not
    // wait behind it, or the endpoint never returns and the tenant's session place stays taken.
    [Fact]
    public async Task ACustomerWhoStopsReading_StillLetsTheSessionEnd()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Floods);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(500), SignalWait = TimeSpan.FromMilliseconds(500) };

        ShellRelayResult result = await RelayAsync(rig, idleSoon).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(ShellEnding.Idle, result.Ending);
    }

    // The same on the guest's side: a command that stops reading stdin leaves the relay's send to stdin waiting.
    [Fact]
    public async Task ACommandThatStopsReading_StillLetsTheSessionEnd()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.IgnoresStdin);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromSeconds(1), SignalWait = TimeSpan.FromMilliseconds(500) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);
        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        using var stopFlooding = new CancellationTokenSource();
        Task flood = Task.Run(async () =>
        {
            var chunk = new byte[64 * 1024];
            try
            {
                for (int i = 0; i < 1024 && !stopFlooding.IsCancellationRequested; i++)
                {
                    await rig.Customer.SendAsync(chunk, WebSocketMessageType.Binary, true, stopFlooding.Token);
                }
            }
            catch (Exception)
            {
            }
        });

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(60));
        stopFlooding.Cancel();

        Assert.Equal(ShellEnding.Idle, result.Ending);
    }

    [Fact]
    public async Task AFragmentedMessageFromTheCustomer_IsCarriedWhole_AndTheSessionGoesOn()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Echo);
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("ab"), WebSocketMessageType.Binary, false, CancellationToken.None);
        await rig.Customer.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, CancellationToken.None);
        await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("c"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("abc", await ReadAsync(rig.Customer, 3));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(ShellEnding.CustomerClosed, (await relay.WaitAsync(TimeSpan.FromSeconds(10))).Ending);
    }

    [Fact]
    public async Task TheCustomersClose_IsAnswered()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Echo);
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ReadUntilClosedAsync(rig.Customer).WaitAsync(TimeSpan.FromSeconds(10)));
        await relay.WaitAsync(TimeSpan.FromSeconds(10));
    }

    // A failed login leaves nc and sshd running in the guest when the end of the stream does not reach sshd: the relay
    // ends the command itself.
    [Fact]
    public async Task ACommandStillRunningAfterTheCustomerLeft_IsSignalled()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.IgnoresEof);
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.CustomerClosedCommandSignalled, result.Ending);
        string signal = Assert.Single(rig.Incus.ControlMessages);
        Assert.Equal("{\"command\":\"signal\",\"signal\":15}", signal);
    }

    [Fact]
    public async Task ACommandThatEnds_ClosesTheCustomersSocketNormally()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.EndsAfterBanner);
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast);

        Assert.Equal(FakeIncusExecServer.Banner, await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length));
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ReadUntilClosedAsync(rig.Customer).WaitAsync(TimeSpan.FromSeconds(10)));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.CommandEnded, result.Ending);
        Assert.Empty(rig.Incus.ControlMessages);
    }

    [Fact]
    public async Task NoBytesForTheIdleTimeout_ClosesTheSession_AndSignalsTheCommand()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.IgnoresEof);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(500) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ReadUntilClosedAsync(rig.Customer).WaitAsync(TimeSpan.FromSeconds(10)));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.Idle, result.Ending);
        Assert.Contains("{\"command\":\"signal\",\"signal\":15}", rig.Incus.ControlMessages);
    }

    // A pair: bytes in either direction alone keep the session open (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session).
    [Fact]
    public async Task BytesFromTheCustomerOnly_KeepTheSessionOpen()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Swallows);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(600) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(250);
            await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("k"), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        Assert.False(relay.IsCompleted);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(ShellEnding.CustomerClosed, (await relay.WaitAsync(TimeSpan.FromSeconds(10))).Ending);
    }

    [Fact]
    public async Task BytesFromTheGuestOnly_KeepTheSessionOpen()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Chatty);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(600) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length + 7);
        Assert.False(relay.IsCompleted);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(ShellEnding.CustomerClosed, (await relay.WaitAsync(TimeSpan.FromSeconds(10))).Ending);
    }

    [Fact]
    public async Task BytesWithinTheIdleTimeout_KeepTheSessionOpen()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Echo);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(600) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(250);
            await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("k"), WebSocketMessageType.Binary, true, CancellationToken.None);
            Assert.Equal("k", await ReadAsync(rig.Customer, 1));
        }
        Assert.False(relay.IsCompleted);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ShellEnding.CustomerClosed, result.Ending);
    }
}
