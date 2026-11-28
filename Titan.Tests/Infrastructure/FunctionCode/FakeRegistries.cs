using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Titan.Tests.Infrastructure.FunctionCode;

/// The code registry, GHCR and GHCR's blob storage host in one handler, over dictionaries: the distribution API as the
/// adapter uses it (HEAD/GET/PUT/DELETE manifests, HEAD/GET blobs, monolithic upload, cross-repository mount), the
/// anonymous GHCR token and the redirect of a GHCR blob to another host.
internal sealed partial class FakeRegistries : HttpMessageHandler, IHttpClientFactory
{
    public const string PushUrl = "http://registry.test:5100";
    public static readonly Uri Ghcr = new("https://ghcr.test/");
    public const string Token = "ghcr-token-0123456789";
    private const string StorageHost = "storage.test";

    // Code registry: blobs by digest, the repositories each blob is linked into, manifests by (repo, tag or digest).
    public readonly Dictionary<string, byte[]> Blobs = new();
    public readonly Dictionary<string, HashSet<string>> Links = new();
    public readonly Dictionary<(string Repo, string Reference), (string MediaType, byte[] Bytes)> Manifests = new();

    // GHCR.
    public readonly Dictionary<string, byte[]> GhcrBlobs = new();
    public readonly Dictionary<(string Repo, string Digest), (string MediaType, byte[] Bytes)> GhcrManifests = new();

    public readonly List<(HttpMethod Method, Uri Uri, string? Authorization)> Seen = new();
    public readonly List<(string Repo, string Digest)> Uploaded = new();
    public readonly List<(string Repo, string Digest, string From)> Mounted = new();

    public bool DeleteDisabled { get; set; }
    public bool MountOpensAnUpload { get; set; }
    public Func<HttpRequestMessage, HttpStatusCode?> Override { get; set; } = _ => null;

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    public static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Link(string repo, string digest) =>
        (Links.TryGetValue(repo, out HashSet<string>? set) ? set : Links[repo] = new HashSet<string>()).Add(digest);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Uri uri = request.RequestUri!;
        Seen.Add((request.Method, uri, request.Headers.Authorization?.ToString()));
        HttpStatusCode? forced = Override(request);
        if (forced is not null)
        {
            return new HttpResponseMessage(forced.Value);
        }
        byte[] body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        return uri.Host switch
        {
            "registry.test" => CodeRegistry(request, uri, body),
            "ghcr.test" => GhcrAnswer(request, uri),
            StorageHost => StorageAnswer(uri),
            _ => new HttpResponseMessage(HttpStatusCode.BadGateway),
        };
    }

    private HttpResponseMessage CodeRegistry(HttpRequestMessage request, Uri uri, byte[] body)
    {
        string path = uri.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Match upload = UploadPath().Match(path);
        if (upload.Success)
        {
            string repo = upload.Groups["repo"].Value;
            string id = upload.Groups["id"].Value;
            if (request.Method == HttpMethod.Post && id.Length == 0)
            {
                string? mount = query["mount"];
                string? from = query["from"];
                bool canMount = mount is not null && from is not null && !MountOpensAnUpload
                                && Links.TryGetValue(from, out HashSet<string>? source) && source.Contains(mount);
                if (canMount)
                {
                    Link(repo, mount!);
                    Mounted.Add((repo, mount!, from!));
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }
                var opened = new HttpResponseMessage(HttpStatusCode.Accepted);
                // Relative, and with a query of its own: the adapter must add the digest with '&'.
                opened.Headers.Location = new Uri($"/v2/{repo}/blobs/uploads/{Guid.NewGuid():N}?_state=s", UriKind.Relative);
                return opened;
            }
            if (request.Method == HttpMethod.Put && id.Length > 0)
            {
                string digest = query["digest"]!;
                bool intact = query["_state"] == "s" && request.Content!.Headers.ContentLength == body.Length && Sha256(body) == digest
                              && request.Content.Headers.ContentType?.MediaType == "application/octet-stream";
                if (!intact)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest);
                }
                Blobs[digest] = body;
                Link(repo, digest);
                Uploaded.Add((repo, digest));
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        Match item = ItemPath().Match(path);
        if (!item.Success)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        string itemRepo = item.Groups["repo"].Value;
        string reference = item.Groups["ref"].Value;
        if (item.Groups["kind"].Value == "blobs")
        {
            bool linked = Links.TryGetValue(itemRepo, out HashSet<string>? set) && set.Contains(reference);
            if (!linked)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return request.Method == HttpMethod.Head
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Blobs[reference]) };
        }

        if (request.Method == HttpMethod.Put)
        {
            string digest = Sha256(body);
            if (reference.StartsWith("sha256:") && reference != digest)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            string mediaType = request.Content!.Headers.ContentType!.MediaType!;
            Manifests[(itemRepo, reference)] = (mediaType, body);
            Manifests[(itemRepo, digest)] = (mediaType, body);
            var stored = new HttpResponseMessage(HttpStatusCode.Created);
            stored.Headers.Add("Docker-Content-Digest", digest);
            return stored;
        }
        if (request.Method == HttpMethod.Delete)
        {
            if (DeleteDisabled)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }
            if (!Manifests.ContainsKey((itemRepo, reference)))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            foreach (var key in Manifests.Where(m => m.Key.Repo == itemRepo && Sha256(m.Value.Bytes) == reference).Select(m => m.Key).ToList())
            {
                Manifests.Remove(key);
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
        if (!Manifests.TryGetValue((itemRepo, reference), out var manifest))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        return request.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : Json(manifest.MediaType, manifest.Bytes);
    }

    private HttpResponseMessage GhcrAnswer(HttpRequestMessage request, Uri uri)
    {
        if (uri.AbsolutePath == "/token")
        {
            bool anonymousPull = uri.Query.Contains("scope=repository:") && uri.Query.EndsWith(":pull&service=ghcr.io");
            return anonymousPull
                ? Json("application/json", Encoding.UTF8.GetBytes($"{{\"token\":\"{Token}\"}}"))
                : new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
        if (request.Headers.Authorization?.ToString() != $"Bearer {Token}")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        Match item = ItemPath().Match(uri.AbsolutePath);
        if (!item.Success)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        string repo = item.Groups["repo"].Value;
        string reference = item.Groups["ref"].Value;
        if (item.Groups["kind"].Value == "manifests")
        {
            return GhcrManifests.TryGetValue((repo, reference), out var m) ? Json(m.MediaType, m.Bytes) : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        if (!GhcrBlobs.ContainsKey(reference))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        redirect.Headers.Location = new Uri($"https://{StorageHost}/blobs/{reference}?signature=signed-url-secret");
        return redirect;
    }

    private HttpResponseMessage StorageAnswer(Uri uri)
    {
        string digest = uri.AbsolutePath["/blobs/".Length..];
        return GhcrBlobs.TryGetValue(digest, out byte[]? blob)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob) }
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string mediaType, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [GeneratedRegex(@"^/v2/(?<repo>.+)/blobs/uploads/(?<id>[^/]*)$")]
    private static partial Regex UploadPath();

    [GeneratedRegex(@"^/v2/(?<repo>.+)/(?<kind>manifests|blobs)/(?<ref>[^/]+)$")]
    private static partial Regex ItemPath();
}
