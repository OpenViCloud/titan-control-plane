using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Titan.Infrastructure.Sessions;

public sealed record ConsoleRelayOptions(TimeSpan IdleTimeout, TimeSpan CloseWait)
{
    public static readonly ConsoleRelayOptions Default = new(IdleTimeout: TimeSpan.FromMinutes(30), CloseWait: TimeSpan.FromSeconds(5));
}

public enum ConsoleEnding
{
    ConsoleEnded,   // Incus ended the console (the VM stopped, Incus closed)
    CustomerClosed, // the customer closed or dropped; closing control ended the console on Incus
    Idle,           // no byte either way for IdleTimeout
}

public sealed record ConsoleRelayResult(long BytesUp, long BytesDown, ConsoleEnding Ending);

// Copies bytes between the customer's websocket and a VM's console on Incus (titan-ops features/vm/design.md#vm-console).
// Binary frames are console bytes both ways. A text message from the customer is a command: only a resize, rebuilt as
// Incus's window-resize, ever reaches control; the customer's text is never forwarded. On "0", Incus ends the console with
// an empty text message, or drops the socket. The relay keeps no byte it copies. Every timeout runs on `clock`.
public static class ConsoleRelay
{
    public const int MaxCommandBytes = 1024;
    public const int MaxWindowSide = 1000;

    public static async Task<ConsoleRelayResult> RunAsync(
        WebSocket customer, WebSocket data, WebSocket control, ConsoleRelayOptions options, TimeProvider clock,
        CancellationToken customerGone)
    {
        var counters = new Counters(clock);
        using var sessionOver = new CancellationTokenSource();
        Task up = CopyUpAsync(customer, data, control, counters, customerGone);
        Task down = CopyDownAsync(data, customer, counters, sessionOver.Token);
        Task<bool> idle = WatchIdleAsync(counters, options.IdleTimeout, clock, sessionOver.Token);

        Task first = await Task.WhenAny(up, down, idle);
        ConsoleEnding ending = ConsoleEnding.Idle;
        if (first == down)
        {
            ending = ConsoleEnding.ConsoleEnded;
        }
        else if (first == up)
        {
            ending = ConsoleEnding.CustomerClosed;
        }

        // Closing control is what ends a console on Incus; it then drops "0", which ends the copy down.
        if (ending != ConsoleEnding.ConsoleEnded)
        {
            await CloseAsync(control, "", options.CloseWait, clock);
            await EndsWithinAsync(down, options.CloseWait, clock);
        }
        await CloseAsync(customer, ending == ConsoleEnding.Idle ? "idle" : "the session ended", options.CloseWait, clock);
        await EndsWithinAsync(up, options.CloseWait, clock);
        if (ending == ConsoleEnding.ConsoleEnded)
        {
            await CloseAsync(control, "", options.CloseWait, clock);
        }
        sessionOver.Cancel();
        await EndsWithinAsync(Task.WhenAll(down, idle), options.CloseWait, clock);
        return new ConsoleRelayResult(counters.Up, counters.Down, ending);
    }

    private sealed class Counters(TimeProvider clock)
    {
        private long _up;
        private long _down;
        private long _lastByteAt = clock.GetTimestamp();

        public long Up => Interlocked.Read(ref _up);
        public long Down => Interlocked.Read(ref _down);
        public TimeSpan SilentFor => clock.GetElapsedTime(Interlocked.Read(ref _lastByteAt));

        public void AddUp(int count)
        {
            Interlocked.Add(ref _up, count);
            Interlocked.Exchange(ref _lastByteAt, clock.GetTimestamp());
        }

        public void AddDown(int count)
        {
            Interlocked.Add(ref _down, count);
            Interlocked.Exchange(ref _lastByteAt, clock.GetTimestamp());
        }
    }

    // Ends when the customer closes or drops the connection.
    private static async Task CopyUpAsync(WebSocket customer, WebSocket data, WebSocket control, Counters counters,
        CancellationToken customerGone)
    {
        var buffer = new byte[64 * 1024];
        var command = new MemoryStream();
        bool commandTooLong = false;
        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await customer.ReceiveAsync(buffer, customerGone);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    if (received.Count == 0)
                    {
                        continue;
                    }
                    await data.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, received.EndOfMessage, CancellationToken.None);
                    counters.AddUp(received.Count);
                    continue;
                }
                commandTooLong = commandTooLong || command.Length + received.Count > MaxCommandBytes;
                if (!commandTooLong)
                {
                    command.Write(buffer, 0, received.Count);
                }
                if (!received.EndOfMessage)
                {
                    continue;
                }
                string? resize = commandTooLong ? null : WindowResizeOf(command.ToArray());
                command.SetLength(0);
                commandTooLong = false;
                if (resize != null)
                {
                    await control.SendAsync(Encoding.UTF8.GetBytes(resize), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }

    /// Incus's window-resize for a customer's {"type":"resize","cols":C,"rows":R}, 1 ≤ C, R ≤ MaxWindowSide; null for
    /// any other text.
    public static string? WindowResizeOf(byte[] text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            bool isResize = root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("type", out JsonElement type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "resize";
            if (!isResize)
            {
                return null;
            }
            int? cols = WindowSideOf(root, "cols");
            int? rows = WindowSideOf(root, "rows");
            if (cols == null || rows == null)
            {
                return null;
            }
            return JsonSerializer.Serialize(new
            {
                command = "window-resize",
                args = new Dictionary<string, string> { ["width"] = cols.Value.ToString(), ["height"] = rows.Value.ToString() },
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? WindowSideOf(JsonElement root, string name)
    {
        bool isNumber = root.TryGetProperty(name, out JsonElement side) && side.ValueKind == JsonValueKind.Number;
        if (!isNumber || !side.TryGetInt32(out int value))
        {
            return null;
        }
        bool inRange = value >= 1 && value <= MaxWindowSide;
        if (!inRange)
        {
            return null;
        }
        return value;
    }

    // Ends when the console does: an empty text message or a close on "0", or the socket dropped.
    private static async Task CopyDownAsync(WebSocket data, WebSocket customer, Counters counters, CancellationToken sessionOver)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await data.ReceiveAsync(buffer, sessionOver);
                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    return;
                }
                counters.AddDown(received.Count);
                await customer.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, received.EndOfMessage, CancellationToken.None);
            }
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }

    // True when the session went IdleTimeout without a byte; false when the session ended first.
    private static async Task<bool> WatchIdleAsync(Counters counters, TimeSpan idleTimeout, TimeProvider clock, CancellationToken sessionOver)
    {
        TimeSpan step = idleTimeout < TimeSpan.FromSeconds(4) ? idleTimeout / 4 : TimeSpan.FromSeconds(1);
        try
        {
            while (true)
            {
                await Task.Delay(step, clock, sessionOver);
                if (counters.SilentFor >= idleTimeout)
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

    private static async Task<bool> EndsWithinAsync(Task task, TimeSpan timeout, TimeProvider clock)
    {
        Task first = await Task.WhenAny(task, Task.Delay(timeout, clock));
        return first == task;
    }

    // A close waits at most `wait`: a send already stuck on a peer that stopped reading holds the socket's send lock.
    private static async Task CloseAsync(WebSocket socket, string reason, TimeSpan wait, TimeProvider clock)
    {
        try
        {
            using var giveUp = new CancellationTokenSource(wait, clock);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, reason, giveUp.Token);
        }
        catch (Exception e) when (IsTheSocketEnding(e))
        {
        }
    }
}
