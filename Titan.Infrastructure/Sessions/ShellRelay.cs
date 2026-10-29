using System.Net.WebSockets;
using System.Text;

namespace Titan.Infrastructure.Sessions;

public sealed record ShellRelayOptions(TimeSpan IdleTimeout, TimeSpan DrainTimeout, TimeSpan SignalWait)
{
    public static readonly ShellRelayOptions Default = new(
        IdleTimeout: TimeSpan.FromMinutes(30), DrainTimeout: TimeSpan.FromSeconds(10), SignalWait: TimeSpan.FromSeconds(5));
}

public enum ShellEnding
{
    CommandEnded,                     // the command in the guest ended (the user logged out, sshd closed)
    CustomerClosed,                   // the customer closed or dropped; the command ended once its stdin did
    CustomerClosedCommandSignalled,   // the customer closed or dropped; the command kept running and was sent SIGTERM
    Idle,                             // no byte either way for IdleTimeout
}

public sealed record ShellRelayResult(long BytesUp, long BytesDown, long BytesStderr, ShellEnding Ending);

// Copies bytes between the customer's websocket and an Incus exec's (titan-ops features/vm/design.md#ssh-session).
// Incus ends a command with an empty message on stdout and drops the socket without a close handshake: both are the
// end of the command, never an error. An empty frame that finishes a fragmented message is not that end. The relay
// keeps no byte it copies.
//
// customerGone (the request's abort token) is for the customer's side only: the Incus side still gets its end of input,
// the drain and the signal after the customer has dropped.
public static class ShellRelay
{
    private static readonly byte[] Sigterm = Encoding.UTF8.GetBytes("{\"command\":\"signal\",\"signal\":15}");

    public static async Task<ShellRelayResult> RunAsync(
        WebSocket customer, WebSocket stdin, WebSocket stdout, WebSocket stderr, WebSocket control,
        ShellRelayOptions options, CancellationToken customerGone)
    {
        var counters = new Counters();
        using var sessionOver = new CancellationTokenSource();
        Task up = CopyUpAsync(customer, stdin, counters, customerGone);
        Task down = CopyDownAsync(stdout, customer, counters, sessionOver.Token);
        Task drainStderr = DrainAsync(stderr, counters, sessionOver.Token);
        Task<bool> idle = WatchIdleAsync(counters, options.IdleTimeout, sessionOver.Token);

        Task first = await Task.WhenAny(up, down, idle);
        ShellEnding ending;
        if (first == down)
        {
            ending = ShellEnding.CommandEnded;
        }
        else if (first == up)
        {
            await CloseStdinAsync(stdin, options.SignalWait);
            bool commandEnded = await EndsWithinAsync(down, options.DrainTimeout);
            ending = ShellEnding.CustomerClosed;
            if (!commandEnded)
            {
                ending = ShellEnding.CustomerClosedCommandSignalled;
                await SignalAsync(control, options.SignalWait);
                await EndsWithinAsync(down, options.SignalWait);
            }
        }
        else
        {
            ending = ShellEnding.Idle;
            await CloseStdinAsync(stdin, options.SignalWait);
            await SignalAsync(control, options.SignalWait);
            await EndsWithinAsync(down, options.SignalWait);
        }

        // Every ending closes the customer's side: it answers a close the customer sent, or starts one.
        await CloseCustomerAsync(customer, ending == ShellEnding.Idle ? "idle" : "the session ended", options.SignalWait);
        await EndsWithinAsync(up, options.SignalWait);
        sessionOver.Cancel();
        await EndsWithinAsync(Task.WhenAll(down, drainStderr, idle), options.SignalWait);
        return new ShellRelayResult(counters.Up, counters.Down, counters.Stderr, ending);
    }

    private sealed class Counters
    {
        private long _up;
        private long _down;
        private long _stderr;
        private long _lastByteAt = Environment.TickCount64;

        public long Up => Interlocked.Read(ref _up);
        public long Down => Interlocked.Read(ref _down);
        public long Stderr => Interlocked.Read(ref _stderr);
        public long LastByteAt => Interlocked.Read(ref _lastByteAt);

        public void AddUp(int count)
        {
            Interlocked.Add(ref _up, count);
            Interlocked.Exchange(ref _lastByteAt, Environment.TickCount64);
        }

        public void AddDown(int count)
        {
            Interlocked.Add(ref _down, count);
            Interlocked.Exchange(ref _lastByteAt, Environment.TickCount64);
        }

        public void AddStderr(int count) => Interlocked.Add(ref _stderr, count);
    }

    // Ends when the customer closes or drops the connection.
    private static async Task CopyUpAsync(WebSocket customer, WebSocket stdin, Counters counters, CancellationToken customerGone)
    {
        var buffer = new byte[64 * 1024];
        bool atMessageStart = true;
        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await customer.ReceiveAsync(buffer, customerGone);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                // An empty whole message carries nothing; forwarding it could read as end of input to the command.
                bool emptyWholeMessage = received.Count == 0 && received.EndOfMessage && atMessageStart;
                atMessageStart = received.EndOfMessage;
                if (emptyWholeMessage)
                {
                    continue;
                }
                await stdin.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, received.EndOfMessage, CancellationToken.None);
                counters.AddUp(received.Count);
            }
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }

    // Ends when the command does. After the customer has gone, stdout is still read to its end, so the end of the
    // command is still seen.
    private static async Task CopyDownAsync(WebSocket stdout, WebSocket customer, Counters counters, CancellationToken sessionOver)
    {
        var buffer = new byte[64 * 1024];
        bool customerGone = false;
        bool atMessageStart = true;
        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await stdout.ReceiveAsync(buffer, sessionOver);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                bool commandEnded = received.Count == 0 && received.EndOfMessage && atMessageStart;
                if (commandEnded)
                {
                    return;
                }
                atMessageStart = received.EndOfMessage;
                counters.AddDown(received.Count);
                if (customerGone)
                {
                    continue;
                }
                try
                {
                    await customer.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, received.EndOfMessage, CancellationToken.None);
                }
                catch (Exception e) when (IsTheSocketEnding(e))
                {
                    customerGone = true;
                }
            }
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }

    private static async Task DrainAsync(WebSocket stderr, Counters counters, CancellationToken sessionOver)
    {
        var buffer = new byte[16 * 1024];
        bool atMessageStart = true;
        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await stderr.ReceiveAsync(buffer, sessionOver);
                bool ended = received.MessageType == WebSocketMessageType.Close
                    || (received.Count == 0 && received.EndOfMessage && atMessageStart);
                if (ended)
                {
                    return;
                }
                atMessageStart = received.EndOfMessage;
                counters.AddStderr(received.Count);
            }
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }

    // True when the session went IdleTimeout without a byte; false when the session ended first.
    private static async Task<bool> WatchIdleAsync(Counters counters, TimeSpan idleTimeout, CancellationToken sessionOver)
    {
        TimeSpan step = idleTimeout < TimeSpan.FromSeconds(4) ? idleTimeout / 4 : TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                await Task.Delay(step, sessionOver);
                long silentFor = Environment.TickCount64 - counters.LastByteAt;
                if (silentFor >= (long)idleTimeout.TotalMilliseconds)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsTheSocketEnding(Exception e) =>
        e is WebSocketException || e is OperationCanceledException || e is ObjectDisposedException || e is InvalidOperationException;

    private static async Task<bool> EndsWithinAsync(Task task, TimeSpan timeout)
    {
        Task first = await Task.WhenAny(task, Task.Delay(timeout));
        return first == task;
    }

    // The sends that end a session wait at most `wait`: a send already stuck on a peer that stopped reading holds the
    // socket's send lock. The endpoint disposes the sockets after the relay returns.
    private static async Task CloseCustomerAsync(WebSocket customer, string reason, TimeSpan wait)
    {
        try
        {
            using var giveUp = new CancellationTokenSource(wait);
            await customer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, giveUp.Token);
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }

    // Closing the sending side of stdin is end-of-input for the command (nc -N then shuts its socket to sshd).
    private static async Task CloseStdinAsync(WebSocket stdin, TimeSpan wait)
    {
        try
        {
            using var giveUp = new CancellationTokenSource(wait);
            await stdin.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", giveUp.Token);
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }

    private static async Task SignalAsync(WebSocket control, TimeSpan wait)
    {
        try
        {
            using var giveUp = new CancellationTokenSource(wait);
            await control.SendAsync(Sigterm, WebSocketMessageType.Text, true, giveUp.Token);
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }
}
