using System.Net;
using System.Security.Cryptography;
using System.Text;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Infrastructure.FunctionCode;

namespace Titan.Tests.Infrastructure.FunctionCode;

// The function code bucket against a stand-in S3 on localhost: the request shape R2 accepts (path-style, one plain body, no
// checksum trailer), a key already gone, and a refusal worded without the key pair.
public sealed class S3FunctionCodeStoreTests : IDisposable
{
    private const string Secret = "S3CR3T-function-0123456789";

    private readonly HttpListener _listener = new();
    private readonly string _endpoint;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "titan-s3-code-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Method, string Path, Dictionary<string, string> Headers, byte[] Body)> _seen = new();
    private Func<HttpListenerRequest, (int Status, string Body)?> _override = _ => null;

    public S3FunctionCodeStoreTests()
    {
        Directory.CreateDirectory(_dir);
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _endpoint = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_endpoint + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose()
    {
        _listener.Close();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }
            using var body = new MemoryStream();
            await ctx.Request.InputStream.CopyToAsync(body);
            var headers = ctx.Request.Headers.AllKeys.ToDictionary(k => k!.ToLowerInvariant(), k => ctx.Request.Headers[k]!);
            lock (_seen)
            {
                _seen.Add((ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, headers, body.ToArray()));
            }
            var (status, text) = _override(ctx.Request) ?? (ctx.Request.HttpMethod == "DELETE" ? 204 : 200, "");
            ctx.Response.StatusCode = status;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    // http, so the stand-in can answer: the https rule is the options' (FunctionArtifactOptionsTests), not the store's.
    private S3FunctionCodeStore Open() => new(new FunctionCodeOptions
    {
        Endpoint = _endpoint,
        Region = "auto",
        Bucket = "titan-function-code-dev",
        AccessKeyId = "key-id",
        SecretAccessKey = Secret,
    }, TimeSpan.FromSeconds(10));

    [Fact]
    public async Task AZip_IsPutAsOnePlainBody_UnderAPathStyleKey()
    {
        string zip = Path.Combine(_dir, "v.zip");
        byte[] content = RandomNumberGenerator.GetBytes(256 * 1024);
        await File.WriteAllBytesAsync(zip, content);
        using var store = Open();

        await store.PutAsync("functions/t/f/v.zip", zip, CancellationToken.None);

        var put = Assert.Single(_seen);
        Assert.Equal("PUT", put.Method);
        Assert.Equal("/titan-function-code-dev/functions/t/f/v.zip", put.Path);
        Assert.Equal(content, put.Body);
        Assert.Equal(content.Length.ToString(), put.Headers["content-length"]);
        Assert.DoesNotContain("aws-chunked", put.Headers.GetValueOrDefault("content-encoding", ""));
        Assert.False(put.Headers.ContainsKey("x-amz-trailer"));
        Assert.Equal("application/zip", put.Headers["content-type"]);
    }

    [Fact]
    public async Task DeletingAKeyAlreadyGone_IsNotAnError()
    {
        _override = r => r.HttpMethod == "DELETE"
            ? (404, "<?xml version=\"1.0\"?><Error><Code>NoSuchKey</Code><Message>gone</Message></Error>")
            : null;
        using var store = Open();

        await store.DeleteAsync("functions/t/f/v.zip", CancellationToken.None);

        Assert.Equal(("DELETE", "/titan-function-code-dev/functions/t/f/v.zip"), (_seen[0].Method, _seen[0].Path));
    }

    [Fact]
    public async Task ARefusal_IsAnArtifactFailure_WordedWithoutTheKeyPair()
    {
        _override = _ => (403, $"<?xml version=\"1.0\"?><Error><Code>AccessDenied</Code><Message>key-id {Secret}</Message></Error>");
        string zip = Path.Combine(_dir, "v.zip");
        await File.WriteAllBytesAsync(zip, [1, 2, 3]);
        using var store = Open();

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(() => store.PutAsync("functions/t/f/v.zip", zip, CancellationToken.None));

        Assert.Equal("function code bucket: access denied (AccessDenied)", e.Message);
        Assert.DoesNotContain(Secret, e.Message);
        Assert.DoesNotContain("key-id", e.Message);
    }

    [Fact]
    public async Task AnUnreachableBucket_IsAnArtifactFailure()
    {
        using var store = new S3FunctionCodeStore(new FunctionCodeOptions
        {
            Endpoint = "http://127.0.0.1:1",
            Region = "auto",
            Bucket = "b",
            AccessKeyId = "key-id",
            SecretAccessKey = Secret,
        }, TimeSpan.FromSeconds(5));

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(() => store.DeleteAsync("k", CancellationToken.None));

        Assert.StartsWith("function code bucket: ", e.Message);
    }

    [Fact]
    public void WithoutAFunctionCluster_TheEmptySection_StillBuildsTheStore()
    {
        using var store = new S3FunctionCodeStore(Microsoft.Extensions.Options.Options.Create(new FunctionCodeOptions()));

        Assert.Empty(_seen);
    }
}
