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

// An in-process HTTPS server that answers a console request the way the Incus 7.0.1 source does (github.com/lxc/incus
// v7.0.1, cmd/incusd/instance_console.go and shared/ws/rwc.go); dev arm64 cannot attach a real serial console, so the
// source is the reference (titan-ops features/vm/design.md#vm-console):
// - POST /1.0/instances/{name}/console answers an async websocket operation with fds "0" and "control"; a second
//   console without force is refused with "This console is already connected. Force is required to take it over."
//   (instance_console.go:572), a stopped instance with 400 "Instance is not running".
// - The console attaches once "0" is connected (:229-232); bytes go both ways as binary messages (rwc.go:77); a text or
//   close message on "0" ends that direction (rwc.go:43); the end of the console is an empty text message (rwc.go:92).
// - A closed "control" ends the console (:265-268); "window-resize" arrives on control with string args (:286).
// - When the console cannot attach (dev arm64), the operation fails and nothing closes the sockets (:232 returns before
//   the defer at :339-354).
// Here the console echoes what it is sent, after a Prompt.
internal sealed class FakeIncusConsoleServer : IAsyncDisposable
{
    public enum Mode
    {
        Echo,            // writes Prompt, then echoes until "0" or control ends
        Swallows,        // writes Prompt, then reads without writing back
        EndsAfterPrompt, // writes Prompt, then ends the console on its own 1.5 s later (the VM stopped)
        CannotAttach,    // the operation fails once "0" connects; the sockets stay open
        EndsAtOnce,      // the console ends as soon as it attaches (the VM stopped at that moment)
        Busy,            // POST console answers "already connected"
        NotRunning,      // POST console answers 400 "Instance is not running"
    }

    public const string Prompt = "login: ";
    public const string CannotAttachError = "Failed to swap console ring buffer with socket: GenericError: Chardev user does not support chardev hotswap";

    private readonly WebApplication _app;
    public string Endpoint { get; }
    public string Fingerprint { get; }
    public Mode Behaviour { get; set; } = Mode.Echo;
    public ConcurrentQueue<string> ConsoleRequests { get; } = new();
    public ConcurrentQueue<string> ControlMessages { get; } = new();
    public HashSet<string> TrustedClientFingerprints { get; }
    // Set when the console ended because control closed or "0" sent its end.
    public TaskCompletionSource ConsoleEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long BytesIn => Interlocked.Read(ref _bytesIn);
    private long _bytesIn;

    private readonly ConcurrentDictionary<string, Operation> _operations = new();

    private sealed class Operation
    {
        public required string Id { get; init; }
        public required Dictionary<string, string> Secrets { get; init; }
        public ConcurrentDictionary<string, WebSocket> Sockets { get; } = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Status { get; set; } = "Running";
        public string Err { get; set; } = "";
    }

    private FakeIncusConsoleServer(WebApplication app, string endpoint, string fingerprint, HashSet<string> trusted)
    {
        _app = app;
        Endpoint = endpoint;
        Fingerprint = fingerprint;
        TrustedClientFingerprints = trusted;
    }

    public static async Task<FakeIncusConsoleServer> StartAsync(params string[] trustedClientFingerprints)
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
        FakeIncusConsoleServer? server = null;
        app.Run(async context => await server!.AnswerAsync(context));
        await app.StartAsync();
        string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new FakeIncusConsoleServer(app, address, TestTlsCertificates.Fingerprint(serverCert), trusted);
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
        bool isConsole = HttpMethods.IsPost(context.Request.Method) && path.EndsWith("/console");
        if (isConsole)
        {
            await StartConsoleAsync(context);
            return;
        }
        bool isWebSocket = path.StartsWith("/1.0/operations/") && path.EndsWith("/websocket");
        if (isWebSocket)
        {
            await ConnectAsync(context, path["/1.0/operations/".Length..^"/websocket".Length]);
            return;
        }
        bool isWait = HttpMethods.IsGet(context.Request.Method) && path.StartsWith("/1.0/operations/") && path.EndsWith("/wait");
        if (isWait)
        {
            string id = path["/1.0/operations/".Length..^"/wait".Length];
            bool known = _operations.TryGetValue(id, out Operation? operation);
            if (!known)
            {
                await WriteAsync(context, 404, """{"type":"error","error_code":404,"error":"Operation not found"}""");
                return;
            }
            int seconds = int.Parse(context.Request.Query["timeout"].ToString());
            await Task.WhenAny(operation!.Done.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
            string body = JsonSerializer.Serialize(new
            {
                type = "sync",
                status = "Success",
                status_code = 200,
                metadata = new { id, @class = "websocket", status = operation.Status, err = operation.Err, metadata = new { } },
            });
            await WriteAsync(context, 200, body);
            return;
        }
        await WriteAsync(context, 404, """{"type":"error","error_code":404,"error":"not found"}""");
    }

    private async Task StartConsoleAsync(HttpContext context)
    {
        string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
        ConsoleRequests.Enqueue(context.Request.Path + context.Request.QueryString + " " + body);
        if (Behaviour == Mode.NotRunning)
        {
            await WriteAsync(context, 400, """{"type":"error","status":"","status_code":0,"operation":"","error_code":400,"error":"Instance is not running","metadata":null}""");
            return;
        }
        if (Behaviour == Mode.Busy)
        {
            await WriteAsync(context, 500, """{"type":"error","status":"","status_code":0,"operation":"","error_code":500,"error":"This console is already connected. Force is required to take it over.","metadata":null}""");
            return;
        }
        var operation = new Operation
        {
            Id = Guid.NewGuid().ToString(),
            Secrets = new[] { "0", "control" }.ToDictionary(fd => fd, _ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))),
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
        WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        operation!.Sockets[fd] = socket;
        if (fd == "control")
        {
            _ = Task.Run(() => ReadControlAsync(operation, socket));
        }
        else
        {
            _ = Task.Run(() => RunConsoleAsync(operation, socket));
        }
        // A console that could not attach leaves its connections open until the client goes.
        if (Behaviour == Mode.CannotAttach)
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted).ContinueWith(_ => { });
            return;
        }
        await operation.Done.Task;
    }

    private async Task ReadControlAsync(Operation operation, WebSocket control)
    {
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await control.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
                ControlMessages.Enqueue(Encoding.UTF8.GetString(buffer, 0, received.Count));
            }
        }
        catch (WebSocketException)
        {
        }
        End(operation, "Success");
    }

    private async Task RunConsoleAsync(Operation operation, WebSocket data)
    {
        if (Behaviour == Mode.CannotAttach)
        {
            operation.Status = "Failure";
            operation.Err = CannotAttachError;
            operation.Done.TrySetResult();
            return;
        }
        if (Behaviour == Mode.EndsAtOnce)
        {
            End(operation, "Success");
            return;
        }
        await data.SendAsync(Encoding.ASCII.GetBytes(Prompt), WebSocketMessageType.Binary, true, CancellationToken.None);
        if (Behaviour == Mode.EndsAfterPrompt)
        {
            // Past the control plane's one read of the operation (IncusShellConnector.ConsoleAttachWaitSeconds), so the
            // console counts as attached.
            await Task.Delay(TimeSpan.FromSeconds(1.5));
            await data.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Text, true, CancellationToken.None);
            End(operation, "Success");
            return;
        }
        var buffer = new byte[64 * 1024];
        try
        {
            while (!operation.Done.Task.IsCompleted)
            {
                WebSocketReceiveResult received = await data.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType != WebSocketMessageType.Binary)
                {
                    break;
                }
                Interlocked.Add(ref _bytesIn, received.Count);
                if (Behaviour == Mode.Swallows)
                {
                    continue;
                }
                await data.SendAsync(buffer.AsMemory(0, received.Count), WebSocketMessageType.Binary, received.EndOfMessage, CancellationToken.None);
            }
        }
        catch (WebSocketException)
        {
        }
        End(operation, "Success");
    }

    // Incus closes both connections when the console ends, without a close handshake.
    private void End(Operation operation, string status)
    {
        if (operation.Status == "Running")
        {
            operation.Status = status;
        }
        ConsoleEnded.TrySetResult();
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
