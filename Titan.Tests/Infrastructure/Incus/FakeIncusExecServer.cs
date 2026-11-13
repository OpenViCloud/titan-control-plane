using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;

namespace Titan.Tests.Infrastructure.Incus;

// An in-process HTTPS server that answers a non-interactive exec the way Incus 7.0.1 did when measured on dev
// (2026-10-13): POST exec answers 202 with an operation
// carrying fds 0, 1, 2 and control; the command starts once all four websockets are connected; the end of the
// command is an empty message on 1 and 2, then the sockets close without a close handshake.
// The command here is an echo: it writes Banner, then echoes stdin to stdout until stdin closes.
internal sealed class FakeIncusExecServer : IAsyncDisposable
{
    public enum Mode
    {
        Echo,              // the guest command echoes stdin and ends at stdin EOF
        IgnoresEof,        // the guest command keeps running after stdin EOF until it gets a signal
        EndsAfterBanner,   // the guest command writes Banner and exits on its own (the user typed exit)
        Swallows,          // the guest command reads stdin and writes nothing back
        Chatty,            // the guest command writes a byte every 200 ms without being asked, until stdin closes
        FragmentedBurst,   // after Banner, 32 KiB as one frame without FIN then an empty final frame (gorilla's large write), then echoes
        StallsUpgrade,     // POST exec answers, the websocket upgrade never does
        Floods,            // after Banner, writes 64 KiB messages as fast as the reader takes them, up to 4 MiB
        IgnoresStdin,      // after Banner, never reads stdin; ends on a signal
        NoExecRoute,       // POST exec answers 404 like a server without the route
        InstanceNotRunning,// POST exec answers 400 "Instance is not running"
        AgentNotRunning,   // POST exec answers 202, the websocket upgrade 500, the operation fails
        EndsWithoutOutput, // the guest command exits 1 without writing a byte (nc with nothing listening on port 22)
        Silent,            // no banner; echoes stdin to stdout, ends at stdin EOF (a server that waits for its client)
    }

    public const string Banner = "SSH-2.0-fake\r\n";
    public const int BurstBytes = 32 * 1024;
    public ConcurrentQueue<WebSocketCloseStatus?> StdinCloseStatuses { get; } = new();

    // Bytes the guest command has read from stdin: a test waits on it before moving a fake clock.
    public long StdinBytes => Interlocked.Read(ref _stdinBytes);
    private long _stdinBytes;

    private readonly WebApplication _app;
    public string Endpoint { get; }
    public string Fingerprint { get; }
    public Mode Behaviour { get; set; } = Mode.Echo;
    public ConcurrentQueue<string> ExecRequests { get; } = new();
    public ConcurrentQueue<string> ControlMessages { get; } = new();
    public HashSet<string> TrustedClientFingerprints { get; }

    private readonly ConcurrentDictionary<string, Operation> _operations = new();

    private sealed class Operation
    {
        public required string Id { get; init; }
        public required Dictionary<string, string> Secrets { get; init; }
        public ConcurrentDictionary<string, WebSocket> Sockets { get; } = new();
        public TaskCompletionSource AllConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Signalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Status { get; set; } = "Running";
        public string Err { get; set; } = "";
        public int? ExitCode { get; set; }
    }

    private FakeIncusExecServer(WebApplication app, string endpoint, string fingerprint, HashSet<string> trusted)
    {
        _app = app;
        Endpoint = endpoint;
        Fingerprint = fingerprint;
        TrustedClientFingerprints = trusted;
    }

    public static async Task<FakeIncusExecServer> StartAsync(params string[] trustedClientFingerprints)
    {
        X509Certificate2 serverCert = TestTlsCertificates.Server();
        var trusted = new HashSet<string>(trustedClientFingerprints);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = serverCert,
                ClientCertificateMode = ClientCertificateMode.AllowCertificate,
                ClientCertificateValidation = (_, _, _) => true,
            })));
        WebApplication app = builder.Build();
        app.UseWebSockets();
        FakeIncusExecServer? server = null;
        app.Run(async context => await server!.AnswerAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeIncusExecServer(app, address, TestTlsCertificates.Fingerprint(serverCert), trusted);
        return server;
    }

    private async Task AnswerAsync(HttpContext context)
    {
        X509Certificate2? client = context.Connection.ClientCertificate;
        bool isTrusted = client != null && TrustedClientFingerprints.Contains(Convert.ToHexStringLower(SHA256.HashData(client.RawData)));
        if (!isTrusted)
        {
            await WriteAsync(context, 403, """{"type":"error","error_code":403,"error":"not authorized"}""");
            return;
        }
        string path = context.Request.Path.Value ?? "";
        bool isExec = HttpMethods.IsPost(context.Request.Method) && path.EndsWith("/exec");
        if (isExec)
        {
            await StartExecAsync(context);
            return;
        }
        bool isWebSocket = path.StartsWith("/1.0/operations/") && path.EndsWith("/websocket");
        if (isWebSocket)
        {
            await ConnectAsync(context, path["/1.0/operations/".Length..^"/websocket".Length]);
            return;
        }
        bool isOperation = HttpMethods.IsGet(context.Request.Method) && path.StartsWith("/1.0/operations/");
        if (isOperation)
        {
            string id = path["/1.0/operations/".Length..];
            bool waits = id.EndsWith("/wait");
            if (waits)
            {
                id = id[..^"/wait".Length];
            }
            bool known = _operations.TryGetValue(id, out Operation? operation);
            if (!known)
            {
                await WriteAsync(context, 404, """{"type":"error","error_code":404,"error":"Operation not found"}""");
                return;
            }
            if (waits)
            {
                int seconds = int.Parse(context.Request.Query["timeout"].ToString());
                await Task.WhenAny(operation!.Done.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            }
            // Incus keeps a finished exec's exit code as "return" in the operation's metadata (measured on Incus 7.0.2,
            // 2026-11-13).
            object metadata = new { };
            if (operation!.ExitCode.HasValue)
            {
                metadata = new { @return = operation.ExitCode.Value };
            }
            string body = JsonSerializer.Serialize(new
            {
                type = "sync",
                status = "Success",
                status_code = 200,
                metadata = new { id, @class = "websocket", status = operation.Status, err = operation.Err, metadata },
            });
            await WriteAsync(context, 200, body);
            return;
        }
        await WriteAsync(context, 404, """{"type":"error","error_code":404,"error":"not found"}""");
    }

    private async Task StartExecAsync(HttpContext context)
    {
        string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
        ExecRequests.Enqueue(context.Request.Path + context.Request.QueryString + " " + body);
        if (Behaviour == Mode.NoExecRoute)
        {
            await WriteAsync(context, 404, """{"type":"error","status":"","status_code":0,"error_code":404,"error":"not found"}""");
            return;
        }
        if (Behaviour == Mode.InstanceNotRunning)
        {
            await WriteAsync(context, 400, """{"type":"error","status":"","status_code":0,"operation":"","error_code":400,"error":"Instance is not running","metadata":null}""");
            return;
        }
        var operation = new Operation
        {
            Id = Guid.NewGuid().ToString(),
            Secrets = new[] { "0", "1", "2", "control" }.ToDictionary(fd => fd, _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))),
        };
        _operations[operation.Id] = operation;
        string answer = JsonSerializer.Serialize(new
        {
            type = "async",
            status = "Operation created",
            status_code = 100,
            operation = "/1.0/operations/" + operation.Id,
            metadata = new { id = operation.Id, @class = "websocket", status = "Running", metadata = new { fds = operation.Secrets } },
        });
        await WriteAsync(context, 202, answer);
    }

    private async Task ConnectAsync(HttpContext context, string operationId)
    {
        bool known = _operations.TryGetValue(operationId, out Operation? operation);
        string secret = context.Request.Query["secret"].ToString();
        string? fd = known ? operation!.Secrets.FirstOrDefault(pair => pair.Value == secret).Key : null;
        if (fd == null || !context.WebSockets.IsWebSocketRequest)
        {
            await WriteAsync(context, 403, """{"type":"error","error_code":403,"error":"bad secret"}""");
            return;
        }
        if (Behaviour == Mode.StallsUpgrade)
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted).ContinueWith(_ => { });
            return;
        }
        if (Behaviour == Mode.AgentNotRunning)
        {
            operation!.Status = "Failure";
            operation.Err = "VM agent isn't currently running";
            await WriteAsync(context, 500, """{"type":"error","error_code":500,"error":"VM agent isn't currently running"}""");
            return;
        }
        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        operation!.Sockets[fd] = socket;
        if (operation.Sockets.Count == 4)
        {
            operation.AllConnected.TrySetResult();
            _ = Task.Run(() => RunCommandAsync(operation));
        }
        if (fd == "control")
        {
            _ = Task.Run(() => ReadControlAsync(operation, socket));
        }
        await operation.Done.Task;
    }

    private async Task ReadControlAsync(Operation operation, WebSocket control)
    {
        var buffer = new byte[4096];
        try
        {
            while (control.State == WebSocketState.Open)
            {
                WebSocketReceiveResult received = await control.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                string message = Encoding.UTF8.GetString(buffer, 0, received.Count);
                ControlMessages.Enqueue(message);
                if (message.Contains("\"signal\""))
                {
                    operation.Signalled.TrySetResult();
                }
            }
        }
        catch (WebSocketException)
        {
        }
    }

    private async Task RunCommandAsync(Operation operation)
    {
        WebSocket stdin = operation.Sockets["0"];
        WebSocket stdout = operation.Sockets["1"];
        WebSocket stderr = operation.Sockets["2"];
        if (Behaviour == Mode.EndsWithoutOutput)
        {
            await EndAsync(operation, stdout, stderr, exitCode: 1);
            return;
        }
        if (Behaviour != Mode.Silent)
        {
            await stdout.SendAsync(Encoding.ASCII.GetBytes(Banner), WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        if (Behaviour == Mode.FragmentedBurst)
        {
            await stdout.SendAsync(new byte[BurstBytes], WebSocketMessageType.Binary, false, CancellationToken.None);
            await stdout.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, CancellationToken.None);
        }
        if (Behaviour == Mode.Floods)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    for (int i = 0; i < 64; i++)
                    {
                        await stdout.SendAsync(new byte[64 * 1024], WebSocketMessageType.Binary, true, CancellationToken.None);
                    }
                }
                catch (WebSocketException)
                {
                }
            });
        }
        using var stopChatter = new CancellationTokenSource();
        if (Behaviour == Mode.Chatty)
        {
            _ = Task.Run(async () =>
            {
                while (!stopChatter.IsCancellationRequested)
                {
                    await Task.Delay(200);
                    await stdout.SendAsync(Encoding.ASCII.GetBytes("."), WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            });
        }
        if (Behaviour == Mode.IgnoresStdin)
        {
            await Task.WhenAny(operation.Signalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        }
        var buffer = new byte[64 * 1024];
        try
        {
            while (Behaviour != Mode.EndsAfterBanner && Behaviour != Mode.IgnoresStdin)
            {
                WebSocketReceiveResult received = await stdin.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    StdinCloseStatuses.Enqueue(received.CloseStatus);
                    break;
                }
                Interlocked.Add(ref _stdinBytes, received.Count);
                bool echoes = Behaviour != Mode.Swallows && Behaviour != Mode.Chatty;
                if (echoes)
                {
                    await stdout.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, received.EndOfMessage, CancellationToken.None);
                }
            }
        }
        catch (WebSocketException)
        {
        }
        stopChatter.Cancel();
        if (Behaviour == Mode.IgnoresEof)
        {
            await Task.WhenAny(operation.Signalled.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        }
        await EndAsync(operation, stdout, stderr, exitCode: 0);
    }

    // Incus ends a command with an empty message on stdout and stderr, then ends the connections without a close
    // handshake: the request handlers return and the server closes the TCP connection in order (not a reset, which
    // could drop bytes the client has not read yet).
    private static async Task EndAsync(Operation operation, WebSocket stdout, WebSocket stderr, int exitCode)
    {
        await stdout.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, CancellationToken.None);
        await stderr.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, CancellationToken.None);
        operation.ExitCode = exitCode;
        operation.Status = "Success";
        operation.Done.TrySetResult();
    }

    private static async Task WriteAsync(HttpContext context, int status, string body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Operation operation in _operations.Values)
        {
            operation.Done.TrySetResult();
        }
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
