using System.Net;
using System.Text;
using Titan.Application.Abstractions;
using Titan.Infrastructure.Backup;

namespace Titan.Tests.Infrastructure;

// The adapter against a stand-in S3 on localhost: what it sends (path-style, no checksum trailer that R2 refuses) and how it
// words a refusal. The real R2 is measured on the dev cluster; this pins the request shape against an SDK upgrade.
public sealed class S3BackupBucketTests : IDisposable
{
    private const string Secret = "S3CR3T-value-0123456789";

    private readonly HttpListener _listener = new();
    private readonly string _endpoint;
    private readonly List<(string Method, string PathAndQuery, Dictionary<string, string> Headers, byte[] Body)> _seen = new();
    private readonly Dictionary<string, byte[]> _store = new();
    private Func<HttpListenerRequest, (int Status, string Body)?> _override = _ => null;
    private TimeSpan _delay = TimeSpan.Zero;

    public S3BackupBucketTests()
    {
        int port = FreePort();
        _endpoint = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(_endpoint + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public void Dispose() => _listener.Close();

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
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
            string target = ctx.Request.Url!.PathAndQuery;
            // Over plain http the SDK frames the payload as signed aws-chunked pieces; over https (R2) it sends it as is.
            byte[] payload = headers.GetValueOrDefault("content-encoding", "").Contains("aws-chunked")
                ? DecodeChunks(body.ToArray())
                : body.ToArray();
            lock (_seen)
            {
                _seen.Add((ctx.Request.HttpMethod, target, headers, payload));
            }

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay);
            }
            var forced = _override(ctx.Request);
            if (forced is not null)
            {
                await Respond(ctx, forced.Value.Status, forced.Value.Body);
                continue;
            }
            string path = ctx.Request.Url.AbsolutePath;
            switch (ctx.Request.HttpMethod)
            {
                case "PUT":
                    _store[path] = payload;
                    ctx.Response.AddHeader("ETag", "\"x\"");
                    await Respond(ctx, 200, "");
                    break;
                case "GET" when ctx.Request.QueryString["list-type"] == "2":
                    string prefix = "/" + path.Trim('/') + "/" + ctx.Request.QueryString["prefix"];
                    var keys = _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal)
                        .Select(k => k[(path.Length)..]).ToList();
                    // Pages of two unless the caller asks for fewer or more, so a listing has to follow continuation tokens.
                    int pageSize = int.TryParse(ctx.Request.QueryString["max-keys"], out int asked) && asked < 100 ? asked : 2;
                    int start = int.TryParse(ctx.Request.QueryString["continuation-token"], out int from) ? from : 0;
                    var page = keys.Skip(start).Take(pageSize).ToList();
                    bool more = start + pageSize < keys.Count;
                    string contents = string.Concat(page.Select(k =>
                        $"<Contents><Key>{k}</Key><LastModified>2026-10-24T14:00:00.000Z</LastModified><Size>{_store[path + k].Length}</Size></Contents>"));
                    await Respond(ctx, 200,
                        "<?xml version=\"1.0\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">"
                        + $"<Name>b</Name><KeyCount>{page.Count}</KeyCount><MaxKeys>{pageSize}</MaxKeys><IsTruncated>{(more ? "true" : "false")}</IsTruncated>"
                        + (more ? $"<NextContinuationToken>{start + pageSize}</NextContinuationToken>" : "")
                        + $"{contents}</ListBucketResult>");
                    break;
                case "GET":
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.OutputStream.WriteAsync(_store[path]);
                    ctx.Response.Close();
                    break;
                case "DELETE":
                    _store.Remove(path);
                    await Respond(ctx, 204, "");
                    break;
            }
        }
    }

    private static byte[] DecodeChunks(byte[] framed)
    {
        var data = new List<byte>();
        int at = 0;
        while (at < framed.Length)
        {
            int lineEnd = Array.IndexOf(framed, (byte)'\r', at);
            string header = Encoding.ASCII.GetString(framed, at, lineEnd - at);
            int size = Convert.ToInt32(header.Split(';')[0], 16);
            if (size == 0)
            {
                break;
            }
            data.AddRange(framed.AsSpan(lineEnd + 2, size).ToArray());
            at = lineEnd + 2 + size + 2;
        }
        return data.ToArray();
    }

    private static async Task Respond(HttpListenerContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private S3BackupBucket Open(string? endpoint = null, TimeSpan? callTimeout = null) =>
        new(new BackupBucketConnection(endpoint ?? _endpoint, "auto", "titan-backup-dev", "key-id", Secret), callTimeout);

    [Fact]
    public async Task WriteListReadDelete_GoThroughPathStyleUrls_AndTheObjectComesBackIntact()
    {
        using var bucket = Open();
        byte[] content = { 1, 2, 3, 4, 5 };

        await bucket.PutAsync("_probe/abc", content, CancellationToken.None);
        var keys = await bucket.ListKeysAsync("_probe/abc", CancellationToken.None);
        byte[] back = await bucket.GetAsync("_probe/abc", CancellationToken.None);
        await bucket.DeleteAsync("_probe/abc", CancellationToken.None);

        Assert.Equal(new[] { "_probe/abc" }, keys);
        Assert.Equal(content, back);
        Assert.Empty(await bucket.ListKeysAsync("_probe/abc", CancellationToken.None));
        Assert.All(_seen, call => Assert.StartsWith("/titan-backup-dev", call.PathAndQuery));
    }

    [Fact]
    public async Task ListingObjects_FollowsPages_AndCarriesSizeAndTime()
    {
        using var bucket = Open();
        for (int i = 0; i < 5; i++)
        {
            await bucket.PutAsync($"postgres/titan-db/wals/{i}.gz", new byte[i + 1], CancellationToken.None);
        }
        await bucket.PutAsync("elsewhere/x", new byte[1], CancellationToken.None);

        var all = await bucket.ListObjectsAsync("postgres/titan-db/", null, CancellationToken.None);
        var first = await bucket.ListObjectsAsync("postgres/titan-db/", 3, CancellationToken.None);

        Assert.Equal(5, all.Count);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, all.Select(o => o.Size).ToArray());
        Assert.All(all, o => Assert.Equal(new DateTimeOffset(2026, 10, 24, 14, 0, 0, TimeSpan.Zero), o.LastModified));
        Assert.Equal(3, first.Count);
    }

    [Fact]
    public async Task AnUpload_CarriesNoChecksumTrailer_ThatR2Refuses_AndNeverTheSecret()
    {
        using var bucket = Open();

        await bucket.PutAsync("_probe/abc", new byte[] { 9, 9, 9 }, CancellationToken.None);

        var put = _seen.Single(call => call.Method == "PUT");
        Assert.DoesNotContain(put.Headers.Keys, h => h.StartsWith("x-amz-checksum-") || h == "x-amz-sdk-checksum-algorithm"
                                                      || h == "x-amz-trailer");
        Assert.Equal(new byte[] { 9, 9, 9 }, put.Body);
        Assert.StartsWith("AWS4-HMAC-SHA256", put.Headers["authorization"]);
        Assert.DoesNotContain(Secret, string.Join(";", put.Headers.Values));
    }

    [Theory]
    [InlineData(403, "AccessDenied", "access denied (AccessDenied)")]
    [InlineData(404, "NoSuchBucket", "the bucket does not exist (NoSuchBucket)")]
    [InlineData(403, "InvalidAccessKeyId", "the access key id or the secret was rejected (InvalidAccessKeyId)")]
    [InlineData(403, "SignatureDoesNotMatch", "the access key id or the secret was rejected (SignatureDoesNotMatch)")]
    [InlineData(500, "InternalError", "the bucket answered with an error (InternalError)")]
    public async Task ARefusal_IsWordedByItsS3Code_NotByTheSdksText(int status, string code, string expected)
    {
        _override = _ => (status, $"<?xml version=\"1.0\"?><Error><Code>{code}</Code><Message>key-id {Secret} rejected</Message></Error>");
        using var bucket = Open();

        var error = await Assert.ThrowsAsync<BackupBucketException>(
            () => bucket.PutAsync("_probe/abc", new byte[] { 1 }, CancellationToken.None));

        Assert.Equal(expected, error.Message);
        Assert.DoesNotContain(Secret, error.Message);
    }

    [Fact]
    public async Task AnEndpointNobodyListensOn_IsCouldNotReach_AndNamesNeitherKeyNorSecret()
    {
        using var bucket = Open($"http://127.0.0.1:{FreePort()}");

        var error = await Assert.ThrowsAsync<BackupBucketException>(
            () => bucket.PutAsync("_probe/abc", new byte[] { 1 }, CancellationToken.None));

        Assert.Equal("could not reach the endpoint", error.Message);
    }

    [Fact]
    public async Task ABucketThatAnswersTooSlowly_IsDidNotAnswer_NotCouldNotReach()
    {
        _delay = TimeSpan.FromSeconds(3);
        using var bucket = Open(callTimeout: TimeSpan.FromMilliseconds(300));

        var error = await Assert.ThrowsAsync<BackupBucketException>(
            () => bucket.PutAsync("_probe/abc", new byte[] { 1 }, CancellationToken.None));

        Assert.Equal("the bucket did not answer in time", error.Message);
    }

    [Theory]
    [InlineData("Slow<Down>", "the bucket answered with an error (500)")]
    [InlineData("", "the bucket answered with an error (500)")]
    [InlineData("AVeryLongCodeThatAHostileEndpointCouldSendToFillTheAdminsScreenWithText0123456789", "the bucket answered with an error (500)")]
    public async Task AnOddErrorCode_IsNotReflectedIntoTheMessage(string code, string expected)
    {
        _override = _ => (500, $"<?xml version=\"1.0\"?><Error><Code>{System.Net.WebUtility.HtmlEncode(code)}</Code><Message>x</Message></Error>");
        using var bucket = Open();

        var error = await Assert.ThrowsAsync<BackupBucketException>(
            () => bucket.PutAsync("_probe/abc", new byte[] { 1 }, CancellationToken.None));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public async Task ACallerWhoCancels_GetsCancellation_NotABucketFailure()
    {
        using var bucket = Open();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bucket.PutAsync("_probe/abc", new byte[] { 1 }, cts.Token));
    }
}
