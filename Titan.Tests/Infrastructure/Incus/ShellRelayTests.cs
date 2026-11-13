using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Titan.Application.Options;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Incus;

// The relay between the customer's websocket and the exec's (titan-ops features/vm/design.md#ssh-session), against an
// Incus that ends a command the way Incus does: an empty message, then sockets dropped without a close handshake.
// Every timeout of the relay runs on a fake clock that only a test moves: a test that asserts the session stays open
// moves it after a sync point; a test that waits for an ending moves it in steps until the relay returns. Real-time
// waits remain only as outer bounds (RealWait) and inside the fake Incus.
public class ShellRelayTests
{
    private static readonly ShellRelayOptions Fast = new(
        IdleTimeout: TimeSpan.FromSeconds(30), DrainTimeout: TimeSpan.FromMilliseconds(300), SignalWait: TimeSpan.FromSeconds(2));

    private static readonly TimeSpan RealWait = TimeSpan.FromSeconds(20);

    private sealed record Rig(FakeIncusExecServer Incus, IncusExecSession Session, WebSocket Server, WebSocket Customer,
        FakeTimeProvider Clock) : IAsyncDisposable
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
        return new Rig(incus, session, server, customer, new FakeTimeProvider());
    }

    private static Task<ShellRelayResult> RelayAsync(Rig rig, ShellRelayOptions options) =>
        RelayAsync(rig, options, CancellationToken.None);

    private static Task<ShellRelayResult> RelayAsync(Rig rig, ShellRelayOptions options, CancellationToken customerGone) =>
        RelayAsync(rig, ShellFirstOutput.Start(rig.Session.Stdout), options, customerGone);

    private static Task<ShellRelayResult> RelayAsync(Rig rig, ShellFirstOutput stdout, ShellRelayOptions options, CancellationToken customerGone) =>
        ShellRelay.RunAsync(rig.Server, rig.Session.Stdin, stdout, rig.Session.Stderr, rig.Session.Control,
            options, rig.Clock, customerGone);

    // Moves the fake clock in small steps until the relay returns: for the endings that need a timeout to pass.
    private static async Task<ShellRelayResult> DriveAsync(Rig rig, Task<ShellRelayResult> relay)
    {
        var giveUp = DateTime.UtcNow + RealWait;
        while (!relay.IsCompleted && DateTime.UtcNow < giveUp)
        {
            rig.Clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }
        return await relay.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var giveUp = DateTime.UtcNow + RealWait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < giveUp, "the condition never held");
            await Task.Delay(5);
        }
    }

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

        ShellRelayResult result = await relay.WaitAsync(RealWait);
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

        ShellRelayResult result = await relay.WaitAsync(RealWait);
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

        ShellRelayResult result = await relay.WaitAsync(RealWait);
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

        ShellRelayResult result = await DriveAsync(rig, relay);
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

        ShellRelayResult result = await DriveAsync(rig, RelayAsync(rig, idleSoon));

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

        ShellRelayResult result = await DriveAsync(rig, relay);
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

        Assert.Equal(ShellEnding.CustomerClosed, (await relay.WaitAsync(RealWait)).Ending);
    }

    [Fact]
    public async Task TheCustomersClose_IsAnswered()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Echo);
        Task<ShellRelayResult> relay = RelayAsync(rig, Fast);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ReadUntilClosedAsync(rig.Customer).WaitAsync(RealWait));
        await relay.WaitAsync(RealWait);
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

        ShellRelayResult result = await DriveAsync(rig, relay);
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
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await ReadUntilClosedAsync(rig.Customer).WaitAsync(RealWait));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(RealWait);
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
        Task<WebSocketCloseStatus?> closed = ReadUntilClosedAsync(rig.Customer);
        Task<ShellRelayResult> driven = DriveAsync(rig, relay);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, await closed.WaitAsync(RealWait));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await driven;
        Assert.Equal(ShellEnding.Idle, result.Ending);
        Assert.Contains("{\"command\":\"signal\",\"signal\":15}", rig.Incus.ControlMessages);
    }

    // A pair: bytes in either direction alone keep the session open (titan-ops features/vm/design.md#ssh-session).
    // The clock moves 500 ms per byte against a 1500 ms idle timeout, and only after the guest has read that byte, so
    // the last byte the relay has stamped is at most 1000 ms old when the watcher looks.
    [Fact]
    public async Task BytesFromTheCustomerOnly_KeepTheSessionOpen()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Swallows);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(1500) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        for (int i = 1; i <= 12; i++)
        {
            await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("k"), WebSocketMessageType.Binary, true, CancellationToken.None);
            int sent = i;
            await UntilAsync(() => rig.Incus.StdinBytes >= sent);
            rig.Clock.Advance(TimeSpan.FromMilliseconds(500));
        }
        Assert.False(relay.IsCompleted);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(ShellEnding.CustomerClosed, (await relay.WaitAsync(RealWait)).Ending);
    }

    // The relay stamps a byte from the guest before it sends it on, so the customer holding the byte means it is stamped.
    [Fact]
    public async Task BytesFromTheGuestOnly_KeepTheSessionOpen()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Chatty);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(600) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(".", await ReadAsync(rig.Customer, 1));
            rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        }
        Assert.False(relay.IsCompleted);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(ShellEnding.CustomerClosed, (await relay.WaitAsync(RealWait)).Ending);
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
            await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("k"), WebSocketMessageType.Binary, true, CancellationToken.None);
            Assert.Equal("k", await ReadAsync(rig.Customer, 1));
            rig.Clock.Advance(TimeSpan.FromMilliseconds(250));
        }
        Assert.False(relay.IsCompleted);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(RealWait);
        Assert.Equal(ShellEnding.CustomerClosed, result.Ending);
    }

    // The idle timeout still ends a session whose last byte is older than it: the fake clock is what decides.
    [Fact]
    public async Task ASessionSilentForTheIdleTimeout_EndsIdle_EvenRightAfterAByte()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Swallows);
        var idleSoon = Fast with { IdleTimeout = TimeSpan.FromMilliseconds(1500), SignalWait = TimeSpan.FromMilliseconds(500) };
        Task<ShellRelayResult> relay = RelayAsync(rig, idleSoon);

        await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length);
        await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("k"), WebSocketMessageType.Binary, true, CancellationToken.None);
        await UntilAsync(() => rig.Incus.StdinBytes >= 1);

        ShellRelayResult result = await DriveAsync(rig, relay);
        Assert.Equal(ShellEnding.Idle, result.Ending);
    }

    // An SSH server writes its banner before it reads anything: nc that ends with no byte found nothing listening.
    [Fact]
    public async Task ACommandThatEndsWithoutAByte_EndedWithoutOutput()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.EndsWithoutOutput);
        ShellFirstOutput stdout = ShellFirstOutput.Start(rig.Session.Stdout);

        Assert.True(await stdout.EndedWithoutOutputWithinAsync(TimeSpan.FromSeconds(5), rig.Clock).WaitAsync(RealWait));
    }

    // The banner read to decide reaches the customer whole, first.
    [Fact]
    public async Task ABanner_IsNotEndedWithoutOutput_AndReachesTheCustomerFirst()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Echo);
        ShellFirstOutput stdout = ShellFirstOutput.Start(rig.Session.Stdout);

        Assert.False(await stdout.EndedWithoutOutputWithinAsync(TimeSpan.FromSeconds(5), rig.Clock).WaitAsync(RealWait));
        Task<ShellRelayResult> relay = RelayAsync(rig, stdout, Fast, CancellationToken.None);
        Assert.Equal(FakeIncusExecServer.Banner, await ReadAsync(rig.Customer, FakeIncusExecServer.Banner.Length));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ShellRelayResult result = await relay.WaitAsync(RealWait);
        Assert.Equal(FakeIncusExecServer.Banner.Length, result.BytesDown);
    }

    // A command still running and silent when the wait is over is not refused: the session goes on, and its first
    // byte, whenever it comes, still reaches the customer.
    [Fact]
    public async Task ACommandSilentForTheWholeWait_IsNotEndedWithoutOutput_AndTheSessionGoesOn()
    {
        await using Rig rig = await StartAsync(FakeIncusExecServer.Mode.Silent);
        ShellFirstOutput stdout = ShellFirstOutput.Start(rig.Session.Stdout);

        Task<bool> ended = stdout.EndedWithoutOutputWithinAsync(TimeSpan.FromSeconds(5), rig.Clock);
        while (!ended.IsCompleted)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }
        Assert.False(await ended);
        Task<ShellRelayResult> relay = RelayAsync(rig, stdout, Fast, CancellationToken.None);
        await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("hi"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("hi", await ReadAsync(rig.Customer, 2));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(ShellEnding.CustomerClosed, (await relay.WaitAsync(RealWait)).Ending);
    }
}
