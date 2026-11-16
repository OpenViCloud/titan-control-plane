using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Titan.Application.Options;
using Titan.Infrastructure.Incus;
using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Incus;

// The relay between the customer's websocket and a VM's console (titan-ops features/vm/design.md#vm-console), against a
// console that behaves as the Incus 7.0.1 source does (FakeIncusConsoleServer). Every timeout of the relay runs on a fake
// clock that only a test moves.
public class ConsoleRelayTests
{
    private static readonly ConsoleRelayOptions Fast = new(IdleTimeout: TimeSpan.FromMinutes(30), CloseWait: TimeSpan.FromSeconds(2));
    private static readonly TimeSpan RealWait = TimeSpan.FromSeconds(20);

    private sealed record Rig(FakeIncusConsoleServer Incus, IncusConsoleSession Session, WebSocket Server, WebSocket Customer,
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

    private static async Task<Rig> StartAsync(FakeIncusConsoleServer.Mode mode)
    {
        var store = IncusTestKit.IdentityStore();
        var titan = await store.GetCertificateAsync(CancellationToken.None);
        var configured = new ClusterOptions();
        FakeIncusConsoleServer incus = await FakeIncusConsoleServer.StartAsync(titan.Fingerprint);
        incus.Behaviour = mode;
        configured.Clusters.Add(IncusTestKit.Cluster("local-mac", incus.Endpoint, incus.Fingerprint));
        IncusConsoleSession session = await new IncusShellConnector(configured, new IncusClientFactory(store))
            .OpenConsoleAsync("local-mac", "t-abc", "vmxyz", CancellationToken.None);
        (WebSocket server, WebSocket customer) = InMemoryWebSocket.Pair();
        return new Rig(incus, session, server, customer, new FakeTimeProvider());
    }

    private static Task<ConsoleRelayResult> RelayAsync(Rig rig) =>
        ConsoleRelay.RunAsync(rig.Server, rig.Session.Data, rig.Session.Control, Fast, rig.Clock, CancellationToken.None);

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

    private static async Task<WebSocketReceiveResult> ReadUntilClosedAsync(WebSocket socket)
    {
        var buffer = new byte[1024];
        while (true)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return received;
            }
        }
    }

    private static Task SendTextAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var giveUp = DateTime.UtcNow + RealWait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < giveUp, "the condition never held");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task Bytes_FlowBothWays_AndTheCustomersClose_EndsTheConsoleOnIncus()
    {
        await using Rig rig = await StartAsync(FakeIncusConsoleServer.Mode.Echo);
        Task<ConsoleRelayResult> relay = RelayAsync(rig);

        Assert.Equal(FakeIncusConsoleServer.Prompt, await ReadAsync(rig.Customer, FakeIncusConsoleServer.Prompt.Length));
        await rig.Customer.SendAsync(Encoding.ASCII.GetBytes("ubuntu\r"), WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal("ubuntu\r", await ReadAsync(rig.Customer, 7));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        ConsoleRelayResult result = await relay.WaitAsync(RealWait);
        Assert.Equal(ConsoleEnding.CustomerClosed, result.Ending);
        Assert.Equal(7, result.BytesUp);
        Assert.Equal(FakeIncusConsoleServer.Prompt.Length + 7, result.BytesDown);
        Task ended = await Task.WhenAny(rig.Incus.ConsoleEnded.Task, Task.Delay(RealWait));
        Assert.Same(rig.Incus.ConsoleEnded.Task, ended);
    }

    // Incus reads window-resize with string arguments (instance_console.go:286); the customer's own text never reaches
    // control.
    [Fact]
    public async Task AResize_ReachesControl_AsWindowResize()
    {
        await using Rig rig = await StartAsync(FakeIncusConsoleServer.Mode.Echo);
        Task<ConsoleRelayResult> relay = RelayAsync(rig);
        await ReadAsync(rig.Customer, FakeIncusConsoleServer.Prompt.Length);

        await SendTextAsync(rig.Customer, """{"type":"resize","cols":120,"rows":40}""");

        await UntilAsync(() => !rig.Incus.ControlMessages.IsEmpty);
        Assert.Equal("""{"command":"window-resize","args":{"width":"120","height":"40"}}""", Assert.Single(rig.Incus.ControlMessages));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await relay.WaitAsync(RealWait);
    }

    [Theory]
    [InlineData("""{"type":"resize","cols":0,"rows":40}""")]
    [InlineData("""{"type":"resize","cols":120,"rows":1001}""")]
    [InlineData("""{"type":"resize","cols":"120","rows":"40"}""")]
    [InlineData("""{"type":"signal","cols":120,"rows":40}""")]
    [InlineData("""{"command":"window-resize","args":{"width":"120","height":"40"}}""")]
    [InlineData("not json")]
    public async Task AnyOtherText_IsDropped(string text)
    {
        await using Rig rig = await StartAsync(FakeIncusConsoleServer.Mode.Echo);
        Task<ConsoleRelayResult> relay = RelayAsync(rig);
        await ReadAsync(rig.Customer, FakeIncusConsoleServer.Prompt.Length);

        await SendTextAsync(rig.Customer, text);
        await SendTextAsync(rig.Customer, """{"type":"resize","cols":80,"rows":24}""");

        await UntilAsync(() => !rig.Incus.ControlMessages.IsEmpty);
        Assert.Equal("""{"command":"window-resize","args":{"width":"80","height":"24"}}""", Assert.Single(rig.Incus.ControlMessages));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await relay.WaitAsync(RealWait);
    }

    // A text message longer than 1 KiB is dropped whole, however it is fragmented; a short one in two frames is one resize.
    [Fact]
    public async Task ALongText_IsDropped_AndAFragmentedResize_IsOne()
    {
        await using Rig rig = await StartAsync(FakeIncusConsoleServer.Mode.Echo);
        Task<ConsoleRelayResult> relay = RelayAsync(rig);
        await ReadAsync(rig.Customer, FakeIncusConsoleServer.Prompt.Length);
        string padded = """{"type":"resize","cols":100,"rows":30,"pad":" """ + new string('x', 2000) + "\"}";

        await rig.Customer.SendAsync(Encoding.UTF8.GetBytes(padded), WebSocketMessageType.Text, true, CancellationToken.None);
        await rig.Customer.SendAsync(Encoding.UTF8.GetBytes("""{"type":"resize","""), WebSocketMessageType.Text, false, CancellationToken.None);
        await rig.Customer.SendAsync(Encoding.UTF8.GetBytes("\"cols\":90,\"rows\":20}"), WebSocketMessageType.Text, true, CancellationToken.None);

        await UntilAsync(() => !rig.Incus.ControlMessages.IsEmpty);
        Assert.Equal("""{"command":"window-resize","args":{"width":"90","height":"20"}}""", Assert.Single(rig.Incus.ControlMessages));
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await relay.WaitAsync(RealWait);
    }

    // The console ended on Incus (the VM stopped): its end is an empty text message (rwc.go:92).
    [Fact]
    public async Task TheConsoleEnding_ClosesTheCustomerNormally()
    {
        await using Rig rig = await StartAsync(FakeIncusConsoleServer.Mode.EndsAfterPrompt);
        Task<ConsoleRelayResult> relay = RelayAsync(rig);

        Assert.Equal(FakeIncusConsoleServer.Prompt, await ReadAsync(rig.Customer, FakeIncusConsoleServer.Prompt.Length));
        WebSocketReceiveResult closed = await ReadUntilClosedAsync(rig.Customer).WaitAsync(RealWait);
        await rig.Customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);

        Assert.Equal(WebSocketCloseStatus.NormalClosure, closed.CloseStatus);
        ConsoleRelayResult result = await relay.WaitAsync(RealWait);
        Assert.Equal(ConsoleEnding.ConsoleEnded, result.Ending);
    }

    // A customer's byte puts the idle clock back, on a console that writes nothing back; 30 minutes without one end the
    // session with reason idle.
    [Fact]
    public async Task ThirtyMinutesWithoutAByte_EndTheSession_AndACustomersByteRestartsTheWait()
    {
        await using Rig rig = await StartAsync(FakeIncusConsoleServer.Mode.Swallows);
        Task<ConsoleRelayResult> relay = RelayAsync(rig);
        await ReadAsync(rig.Customer, FakeIncusConsoleServer.Prompt.Length);

        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        await rig.Customer.SendAsync(new byte[] { 0x0d }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await UntilAsync(() => rig.Incus.BytesIn == 1);
        Task<WebSocketReceiveResult> closed = ReadUntilClosedAsync(rig.Customer);
        rig.Clock.Advance(TimeSpan.FromMinutes(20));
        await Task.Delay(300);
        Assert.False(closed.IsCompleted, "the session closed 20 minutes after the customer's byte");

        var giveUp = DateTime.UtcNow + RealWait;
        while (!relay.IsCompleted && DateTime.UtcNow < giveUp)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(5);
        }
        ConsoleRelayResult result = await relay.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ConsoleEnding.Idle, result.Ending);
        Assert.Equal("idle", (await closed.WaitAsync(RealWait)).CloseStatusDescription);
    }
}
