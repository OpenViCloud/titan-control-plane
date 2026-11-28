using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;

namespace Titan.Infrastructure.FunctionCode;

/// The code registry over the OCI distribution API, no Docker and no buildah (titan-ops features/ovc-function/design.md#code-source,
/// titan-ops features/ovc-function/design.md#code-storage). Messages name the method, the path and the status, never a token.
public sealed partial class RegistryFunctionImageRegistry : IFunctionImageRegistry
{
    public const string CodeRegistryClient = "function-code-registry";
    public const string GhcrClient = "function-runtime-ghcr";

    public const string OciManifest = "application/vnd.oci.image.manifest.v1+json";
    public const string DockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    public const string OciLayer = "application/vnd.oci.image.layer.v1.tar+gzip";
    public const string DockerLayer = "application/vnd.docker.image.rootfs.diff.tar.gzip";

    private const string RuntimeRepoPrefix = "runtime/";
    private const int MaxRedirects = 5;
    private static readonly Uri DefaultGhcr = new("https://ghcr.io/");

    // Compact, and without the default encoder's + for every '+' in a media type: the bytes are what the digest names.
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IHttpClientFactory _clients;
    private readonly IOptions<FunctionRegistryOptions> _options;
    private readonly Uri _ghcr;

    public RegistryFunctionImageRegistry(IHttpClientFactory clients, IOptions<FunctionRegistryOptions> options)
        : this(clients, options, DefaultGhcr)
    {
    }

    internal RegistryFunctionImageRegistry(IHttpClientFactory clients, IOptions<FunctionRegistryOptions> options, Uri ghcr)
    {
        _clients = clients;
        _options = options;
        _ghcr = ghcr;
    }

    public async Task EnsureRuntimeCopyAsync(string runtimeName, string runtimeImage, CancellationToken ct)
    {
        string runtimeRepo = RuntimeRepo(runtimeName);
        (string sourceRepo, string digest) = ParseGhcrImage(runtimeImage);
        HttpClient registry = _clients.CreateClient(CodeRegistryClient);

        if (await ExistsAsync(registry, Code($"/v2/{runtimeRepo}/manifests/{digest}"), ManifestAccept, "code registry", ct))
        {
            return;
        }

        HttpClient ghcr = _clients.CreateClient(GhcrClient);
        string token = await GhcrTokenAsync(ghcr, sourceRepo, ct);
        byte[] manifestBytes = await GhcrManifestAsync(ghcr, sourceRepo, digest, token, ct);
        (string mediaType, JsonObject manifest) = ReadManifest(manifestBytes, digest, "GHCR");

        foreach ((string blobDigest, long size) in Blobs(manifest))
        {
            Uri link = Code($"/v2/{runtimeRepo}/blobs/{blobDigest}");
            if (await ExistsAsync(registry, link, null, "code registry", ct))
            {
                continue;
            }
            using HttpResponseMessage blob = await GhcrBlobAsync(ghcr, sourceRepo, blobDigest, token, ct);
            await using Stream body = await blob.Content.ReadAsStreamAsync(ct);
            // The registry checks the digest the upload names against the bytes it received.
            await UploadAsync(registry, runtimeRepo, blobDigest, new StreamContent(body), size, ct);
        }

        await PutManifestAsync(registry, runtimeRepo, digest, mediaType, manifestBytes, ct);
    }

    public async Task<string> PushFunctionImageAsync(string runtimeName, string runtimeImage, Guid tenantId, Guid functionId,
        Guid versionId, FunctionCodeLayer layer, CancellationToken ct)
    {
        string runtimeRepo = RuntimeRepo(runtimeName);
        (_, string runtimeDigest) = ParseGhcrImage(runtimeImage);
        string repo = FunctionRepo(tenantId, functionId);
        HttpClient registry = _clients.CreateClient(CodeRegistryClient);

        byte[] runtimeManifestBytes = await GetBytesAsync(registry, Code($"/v2/{runtimeRepo}/manifests/{runtimeDigest}"), ManifestAccept, ct);
        (string mediaType, JsonObject manifest) = ReadManifest(runtimeManifestBytes, runtimeDigest, "code registry");
        JsonObject runtimeConfig = (JsonObject)manifest["config"]!;
        string runtimeConfigDigest = (string)runtimeConfig["digest"]!;
        byte[] configBytes = await GetBytesAsync(registry, Code($"/v2/{runtimeRepo}/blobs/{runtimeConfigDigest}"), null, ct);
        if (Sha256(configBytes) != runtimeConfigDigest)
        {
            throw new FunctionArtifactException($"code registry: the config of {runtimeRepo} does not match its digest");
        }
        JsonObject config = JsonNode.Parse(configBytes) as JsonObject
            ?? throw new FunctionArtifactException($"code registry: the config of {runtimeRepo} is not a JSON object");
        if ((string?)config["architecture"] != "amd64" || (string?)config["os"] != "linux")
        {
            throw new FunctionArtifactException($"code registry: {runtimeRepo} is not a linux/amd64 image");
        }

        foreach (JsonNode? runtimeLayer in (JsonArray)manifest["layers"]!)
        {
            await MountAsync(registry, repo, (string)runtimeLayer!["digest"]!, runtimeRepo, ct);
        }

        Uri codeLayer = Code($"/v2/{repo}/blobs/{layer.Digest}");
        if (!await ExistsAsync(registry, codeLayer, null, "code registry", ct))
        {
            await using FileStream file = new(layer.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await UploadAsync(registry, repo, layer.Digest, new StreamContent(file), layer.Bytes, ct);
        }

        JsonObject rootfs = config["rootfs"] as JsonObject
            ?? throw new FunctionArtifactException($"code registry: the config of {runtimeRepo} has no rootfs");
        (rootfs["diff_ids"] as JsonArray ?? throw new FunctionArtifactException($"code registry: the config of {runtimeRepo} has no diff_ids"))
            .Add(layer.DiffId);
        if (config["history"] is not JsonArray history)
        {
            history = [];
            config["history"] = history;
        }
        history.Add(new JsonObject { ["created"] = "1970-01-01T00:00:00Z", ["created_by"] = "titan function code" });
        byte[] newConfig = JsonSerializer.SerializeToUtf8Bytes(config, Compact);
        string newConfigDigest = Sha256(newConfig);
        if (!await ExistsAsync(registry, Code($"/v2/{repo}/blobs/{newConfigDigest}"), null, "code registry", ct))
        {
            await UploadAsync(registry, repo, newConfigDigest, new ByteArrayContent(newConfig), newConfig.Length, ct);
        }

        manifest["config"] = new JsonObject
        {
            ["mediaType"] = (string?)runtimeConfig["mediaType"],
            ["digest"] = newConfigDigest,
            ["size"] = newConfig.Length,
        };
        ((JsonArray)manifest["layers"]!).Add(new JsonObject
        {
            ["mediaType"] = mediaType == DockerManifest ? DockerLayer : OciLayer,
            ["digest"] = layer.Digest,
            ["size"] = layer.Bytes,
        });
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Compact);
        string digest = Sha256(manifestBytes);
        await PutManifestAsync(registry, repo, versionId.ToString("D"), mediaType, manifestBytes, ct, expectedDigest: digest);
        return digest;
    }

    public async Task<bool> HasFunctionImageAsync(Guid tenantId, Guid functionId, string digest, CancellationToken ct)
    {
        RequireDigest(digest);
        HttpClient registry = _clients.CreateClient(CodeRegistryClient);
        return await ExistsAsync(registry, Code($"/v2/{FunctionRepo(tenantId, functionId)}/manifests/{digest}"), ManifestAccept,
            "code registry", ct);
    }

    public async Task DeleteFunctionImageAsync(Guid tenantId, Guid functionId, string digest, CancellationToken ct)
    {
        RequireDigest(digest);
        HttpClient registry = _clients.CreateClient(CodeRegistryClient);
        using var request = new HttpRequestMessage(HttpMethod.Delete, Code($"/v2/{FunctionRepo(tenantId, functionId)}/manifests/{digest}"));
        using HttpResponseMessage response = await SendAsync(registry, request, "code registry", HttpCompletionOption.ResponseContentRead, ct);
        // 405 is a registry started without REGISTRY_STORAGE_DELETE_ENABLED: nothing would ever be freed, so it is a failure.
        bool done = response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.NotFound;
        if (!done)
        {
            throw Failure("code registry", request, response.StatusCode);
        }
    }

    public string PullReference(Guid tenantId, Guid functionId, string digest)
    {
        RequireDigest(digest);
        return $"{_options.Value.PullHost}/{FunctionRepo(tenantId, functionId)}@{digest}";
    }

    private static string FunctionRepo(Guid tenantId, Guid functionId) => $"fn/{tenantId:D}/{functionId:D}";

    private static string RuntimeRepo(string runtimeName)
    {
        if (!RepoComponent().IsMatch(runtimeName))
        {
            throw new ArgumentException($"'{runtimeName}' is not a registry repository name component.", nameof(runtimeName));
        }
        return RuntimeRepoPrefix + runtimeName;
    }

    private static (string Repo, string Digest) ParseGhcrImage(string image)
    {
        Match match = GhcrImage().Match(image);
        if (!match.Success)
        {
            throw new ArgumentException("A runtime image must be ghcr.io/<repository>@sha256:<64 hex>.", nameof(image));
        }
        return (match.Groups["repo"].Value, match.Groups["digest"].Value);
    }

    private static void RequireDigest(string digest)
    {
        if (!DigestPattern().IsMatch(digest))
        {
            throw new ArgumentException("A digest must be sha256:<64 hex>.", nameof(digest));
        }
    }

    private Uri Code(string pathAndQuery) => new(new Uri(_options.Value.PushUrl.Trim()), pathAndQuery);

    private Uri Ghcr(string pathAndQuery) => new(_ghcr, pathAndQuery);

    private static readonly string[] ManifestAccept = [OciManifest, DockerManifest];

    /// One platform's manifest, checked against the digest it was asked by: an index would need a choice of platform the
    /// function cluster does not make (one linux/amd64 manifest per runtime image).
    private static (string MediaType, JsonObject Manifest) ReadManifest(byte[] bytes, string digest, string source)
    {
        if (Sha256(bytes) != digest)
        {
            throw new FunctionArtifactException($"{source}: the manifest {digest} does not match its digest");
        }
        JsonObject? manifest;
        try
        {
            manifest = JsonNode.Parse(bytes) as JsonObject;
        }
        catch (JsonException)
        {
            manifest = null;
        }
        string? mediaType = (string?)manifest?["mediaType"];
        bool isOnePlatform = manifest is not null && manifest["manifests"] is null
            && manifest["config"] is JsonObject && manifest["layers"] is JsonArray
            && mediaType is OciManifest or DockerManifest;
        if (!isOnePlatform)
        {
            throw new FunctionArtifactException(
                $"{source}: the manifest {digest} is not a single-platform OCI or Docker v2 image manifest");
        }
        return (mediaType!, manifest!);
    }

    private static IEnumerable<(string Digest, long Size)> Blobs(JsonObject manifest)
    {
        yield return ((string)manifest["config"]!["digest"]!, (long)manifest["config"]!["size"]!);
        foreach (JsonNode? layer in (JsonArray)manifest["layers"]!)
        {
            yield return ((string)layer!["digest"]!, (long)layer["size"]!);
        }
    }

    private async Task<string> GhcrTokenAsync(HttpClient ghcr, string repo, CancellationToken ct)
    {
        // Anonymous: the runtime packages are public (titan-ops features/ovc-function/design.md#runtimes).
        byte[] body = await GetBytesAsync(ghcr, Ghcr($"/token?scope=repository:{repo}:pull&service=ghcr.io"), null, ct, "GHCR");
        string? token = null;
        try
        {
            token = (string?)JsonNode.Parse(body)?["token"];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
        }
        return string.IsNullOrEmpty(token) ? throw new FunctionArtifactException("GHCR: the token answer has no token") : token;
    }

    private async Task<byte[]> GhcrManifestAsync(HttpClient ghcr, string repo, string digest, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Ghcr($"/v2/{repo}/manifests/{digest}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        AddAccept(request, ManifestAccept);
        using HttpResponseMessage response = await SendAsync(ghcr, request, "GHCR", HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Failure("GHCR", request, response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// GHCR answers a blob with a redirect to its storage host. The token goes to GHCR only: the GHCR client follows no
    /// redirect by itself, and each hop off GHCR's host goes without Authorization.
    private async Task<HttpResponseMessage> GhcrBlobAsync(HttpClient ghcr, string repo, string digest, string token, CancellationToken ct)
    {
        Uri target = Ghcr($"/v2/{repo}/blobs/{digest}");
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (string.Equals(target.Host, _ghcr.Host, StringComparison.OrdinalIgnoreCase) && target.Scheme == _ghcr.Scheme)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            HttpResponseMessage response = await SendAsync(ghcr, request, "GHCR", HttpCompletionOption.ResponseHeadersRead, ct);
            int status = (int)response.StatusCode;
            if (status == 200)
            {
                return response;
            }
            Uri? location = response.Headers.Location;
            response.Dispose();
            bool isRedirect = status is 301 or 302 or 303 or 307 or 308 && location is not null;
            if (!isRedirect)
            {
                throw Failure("GHCR", request, (HttpStatusCode)status);
            }
            target = location!.IsAbsoluteUri ? location : new Uri(target, location);
            if (target.Scheme != Uri.UriSchemeHttps && _ghcr.Scheme == Uri.UriSchemeHttps)
            {
                throw new FunctionArtifactException($"GHCR: GET {request.RequestUri!.AbsolutePath} redirected off https");
            }
        }
        throw new FunctionArtifactException($"GHCR: GET /v2/{repo}/blobs/{digest} redirected more than {MaxRedirects} times");
    }

    private async Task MountAsync(HttpClient registry, string repo, string digest, string fromRepo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            Code($"/v2/{repo}/blobs/uploads/?mount={digest}&from={fromRepo}"));
        using HttpResponseMessage response = await SendAsync(registry, request, "code registry", HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode == HttpStatusCode.Created)
        {
            return;
        }
        if (response.StatusCode == HttpStatusCode.Accepted)
        {
            // The registry opened an upload instead: the layer is not in the runtime copy, and the function image must not
            // hold a copy of its own (titan-ops features/ovc-function/design.md#code-storage).
            throw new FunctionArtifactException(
                $"code registry: the mount of {digest} from {fromRepo} into {repo} was not done (202, an upload was opened instead)");
        }
        throw Failure("code registry", request, response.StatusCode);
    }

    // Monolithic upload: POST opens it, one PUT carries the whole blob and names its digest.
    private async Task UploadAsync(HttpClient registry, string repo, string digest, HttpContent body, long size, CancellationToken ct)
    {
        using HttpContent content = body;
        Uri start = Code($"/v2/{repo}/blobs/uploads/");
        using var open = new HttpRequestMessage(HttpMethod.Post, start);
        Uri location;
        using (HttpResponseMessage opened = await SendAsync(registry, open, "code registry", HttpCompletionOption.ResponseContentRead, ct))
        {
            if (opened.StatusCode != HttpStatusCode.Accepted || opened.Headers.Location is null)
            {
                throw Failure("code registry", open, opened.StatusCode);
            }
            location = opened.Headers.Location.IsAbsoluteUri ? opened.Headers.Location : new Uri(start, opened.Headers.Location);
        }
        string separator = location.Query.Length > 0 ? "&" : "?";
        using var put = new HttpRequestMessage(HttpMethod.Put, new Uri(location + separator + "digest=" + Uri.EscapeDataString(digest)))
        {
            Content = content,
        };
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = size;
        using HttpResponseMessage stored = await SendAsync(registry, put, "code registry", HttpCompletionOption.ResponseContentRead, ct);
        if (stored.StatusCode != HttpStatusCode.Created)
        {
            throw Failure("code registry", put, stored.StatusCode);
        }
    }

    private async Task PutManifestAsync(HttpClient registry, string repo, string reference, string mediaType, byte[] bytes,
        CancellationToken ct, string? expectedDigest = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, Code($"/v2/{repo}/manifests/{reference}"))
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        using HttpResponseMessage response = await SendAsync(registry, request, "code registry", HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            throw Failure("code registry", request, response.StatusCode);
        }
        bool saysADigest = response.Headers.TryGetValues("Docker-Content-Digest", out IEnumerable<string>? said);
        string? stored = saysADigest ? said!.FirstOrDefault() : null;
        if (expectedDigest is not null && stored is not null && stored != expectedDigest)
        {
            throw new FunctionArtifactException($"code registry: the manifest of {repo} was stored as {stored}, not {expectedDigest}");
        }
    }

    private static async Task<bool> ExistsAsync(HttpClient client, Uri uri, string[]? accept, string source, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, uri);
        if (accept is not null)
        {
            AddAccept(request, accept);
        }
        using HttpResponseMessage response = await SendAsync(client, request, source, HttpCompletionOption.ResponseHeadersRead, ct);
        return response.StatusCode switch
        {
            HttpStatusCode.OK => true,
            HttpStatusCode.NotFound => false,
            _ => throw Failure(source, request, response.StatusCode),
        };
    }

    private static async Task<byte[]> GetBytesAsync(HttpClient client, Uri uri, string[]? accept, CancellationToken ct,
        string source = "code registry")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (accept is not null)
        {
            AddAccept(request, accept);
        }
        using HttpResponseMessage response = await SendAsync(client, request, source, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw Failure(source, request, response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static void AddAccept(HttpRequestMessage request, string[] mediaTypes)
    {
        foreach (string mediaType in mediaTypes)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, string source,
        HttpCompletionOption completion, CancellationToken ct)
    {
        try
        {
            return await client.SendAsync(request, completion, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new FunctionArtifactException($"{source}: {Describe(request)} did not answer in time");
        }
        catch (HttpRequestException)
        {
            throw new FunctionArtifactException($"{source}: {Describe(request)} could not be sent");
        }
    }

    private static FunctionArtifactException Failure(string source, HttpRequestMessage request, HttpStatusCode status) =>
        new($"{source}: {Describe(request)} answered {(int)status}");

    // Path only: a storage redirect's query carries a signature.
    private static string Describe(HttpRequestMessage request) =>
        $"{request.Method} {request.RequestUri?.Host}{request.RequestUri?.AbsolutePath}";

    private static string Sha256(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    [GeneratedRegex(@"^ghcr\.io/(?<repo>[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*)@(?<digest>sha256:[0-9a-f]{64})$")]
    private static partial Regex GhcrImage();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}$")]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$")]
    private static partial Regex RepoComponent();
}
