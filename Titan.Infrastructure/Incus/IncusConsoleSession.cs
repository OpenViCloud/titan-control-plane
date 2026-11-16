using System.Net.WebSockets;

namespace Titan.Infrastructure.Incus;

// The Incus half of a console session (titan-ops features/vm/design.md#vm-console): "0" carries the console's bytes both
// ways, "control" its window size; Incus ends the console when control closes.
public sealed class IncusConsoleSession(IncusClient client, string operationId, WebSocket data, WebSocket control)
    : IAsyncDisposable
{
    public string OperationId { get; } = operationId;
    public WebSocket Data { get; } = data;
    public WebSocket Control { get; } = control;

    private static readonly TimeSpan CloseWait = TimeSpan.FromSeconds(5);

    // Closing control ends the console on Incus, so the next console of the VM is not refused as busy.
    public async ValueTask DisposeAsync()
    {
        await CloseAsync(Control);
        Data.Dispose();
        Control.Dispose();
        client.Dispose();
    }

    private static async Task CloseAsync(WebSocket socket)
    {
        bool open = socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived;
        if (!open)
        {
            return;
        }
        try
        {
            using var giveUp = new CancellationTokenSource(CloseWait);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", giveUp.Token);
        }
        catch (Exception e) when (e is WebSocketException || e is OperationCanceledException || e is ObjectDisposedException)
        {
        }
    }
}
