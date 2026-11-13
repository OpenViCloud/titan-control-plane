using System.Net.WebSockets;

namespace Titan.Infrastructure.Sessions;

// The first receive on an SSH session's stdout, started before the customer's upgrade
// (titan-ops features/vm/design.md#ssh-session). An SSH server writes its banner before it reads anything, so nc that
// ends without a byte found nothing listening on port 22: in a VM whose agent is up while cloud-init still holds sshd.
// The receive is never cancelled (a cancelled receive aborts the socket): the relay takes it over, and the byte it
// brings is the first one the customer gets.
public sealed class ShellFirstOutput
{
    private ShellFirstOutput(WebSocket stdout, byte[] buffer, Task<WebSocketReceiveResult> receive)
    {
        Stdout = stdout;
        Buffer = buffer;
        Receive = receive;
    }

    public WebSocket Stdout { get; }
    internal byte[] Buffer { get; }
    internal Task<WebSocketReceiveResult> Receive { get; }

    public static ShellFirstOutput Start(WebSocket stdout)
    {
        var buffer = new byte[64 * 1024];
        return new ShellFirstOutput(stdout, buffer, stdout.ReceiveAsync(buffer, CancellationToken.None));
    }

    // True when the command ended within `wait` without writing a byte, or its stdout socket dropped before one (the
    // session cannot open either way, and a retry is the customer's move); false when a byte came, and also when the
    // command is still silent at the end of the wait (a slow server is not refused).
    public async Task<bool> EndedWithoutOutputWithinAsync(TimeSpan wait, TimeProvider clock)
    {
        Task first = await Task.WhenAny(Receive, Task.Delay(wait, clock));
        if (first != Receive)
        {
            return false;
        }
        bool dropped = Receive.IsFaulted || Receive.IsCanceled;
        if (dropped)
        {
            _ = Receive.Exception;
            return true;
        }
        WebSocketReceiveResult received = Receive.Result;
        return received.MessageType == WebSocketMessageType.Close || (received.Count == 0 && received.EndOfMessage);
    }
}
