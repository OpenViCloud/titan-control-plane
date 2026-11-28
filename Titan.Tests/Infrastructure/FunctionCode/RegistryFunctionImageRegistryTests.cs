using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Infrastructure.FunctionCode;

namespace Titan.Tests.Infrastructure.FunctionCode;

// The registry steps of titan-ops features/ovc-function/design.md#code-source against an in-process registry: the runtime
// copied from GHCR once, the function image built from mounted runtime layers, the code layer and an extended config.
public sealed class RegistryFunctionImageRegistryTests : IDisposable
{
    private const string Runtime = "python3.15";
    private const string RuntimeRepo = "runtime/python3.15";
    private const string GhcrRepo = "openvicloud/titan-function-runtimes/python3.15";
    private static readonly Guid TenantId = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000000a");
    private static readonly Guid FunctionId = Guid.Parse("0a1b2c3d-0000-4000-8000-00000000000f");
    private static readonly Guid VersionId = Guid.Parse("0a1b2c3d-0000-4000-8000-0000000000aa");

    private readonly FakeRegistries _fake = new();
    private readonly RegistryFunctionImageRegistry _registry;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "titan-registry-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _runtimeLayers = new();
    private string _runtimeConfigDigest = "";

    public RegistryFunctionImageRegistryTests()
    {
        Directory.CreateDirectory(_dir);
        var options = Options.Create(new FunctionRegistryOptions { PushUrl = FakeRegistries.PushUrl, PullHost = "localhost:5100" });
        _registry = new RegistryFunctionImageRegistry(_fake, options, FakeRegistries.Ghcr);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PublishRuntime(string mediaType = RegistryFunctionImageRegistry.OciManifest, string architecture = "amd64")
    {
        string layerType = mediaType == RegistryFunctionImageRegistry.DockerManifest
            ? RegistryFunctionImageRegistry.DockerLayer
            : RegistryFunctionImageRegistry.OciLayer;
        var layers = new JsonArray();
        var diffIds = new JsonArray();
        foreach (int size in new[] { 3000, 5000 })
        {
            byte[] blob = RandomNumberGenerator.GetBytes(size);
            string digest = FakeRegistries.Sha256(blob);
            _fake.GhcrBlobs[digest] = blob;
            _runtimeLayers.Add(digest);
            layers.Add(new JsonObject { ["mediaType"] = layerType, ["digest"] = digest, ["size"] = size });
            diffIds.Add(FakeRegistries.Sha256(RandomNumberGenerator.GetBytes(8)));
        }
        byte[] config = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["architecture"] = architecture,
            ["os"] = "linux",
            ["config"] = new JsonObject { ["Entrypoint"] = new JsonArray("/opt/titan/adapter"), ["Env"] = new JsonArray("A=b+c") },
            ["rootfs"] = new JsonObject { ["type"] = "layers", ["diff_ids"] = diffIds },
            ["history"] = new JsonArray(new JsonObject { ["created_by"] = "base" }),
        }.ToJsonString());
        _runtimeConfigDigest = FakeRegistries.Sha256(config);
        _fake.GhcrBlobs[_runtimeConfigDigest] = config;
        byte[] manifest = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = mediaType,
            ["config"] = new JsonObject
            {
                ["mediaType"] = "application/vnd.oci.image.config.v1+json",
                ["digest"] = _runtimeConfigDigest,
                ["size"] = config.Length,
            },
            ["layers"] = layers,
        }.ToJsonString());
        string manifestDigest = FakeRegistries.Sha256(manifest);
        _fake.GhcrManifests[(GhcrRepo, manifestDigest)] = (mediaType, manifest);
        return $"ghcr.io/{GhcrRepo}@{manifestDigest}";
    }

    private FunctionCodeLayer CodeLayer()
    {
        string path = Path.Combine(_dir, "code.tar.gz");
        byte[] gz = RandomNumberGenerator.GetBytes(4096);
        File.WriteAllBytes(path, gz);
        string diffId = FakeRegistries.Sha256(RandomNumberGenerator.GetBytes(16));
        return new FunctionCodeLayer("sha256:" + new string('0', 64), 10, 20, 1, diffId, FakeRegistries.Sha256(gz), gz.Length, path);
    }

    private static string Repo => $"fn/{TenantId:D}/{FunctionId:D}";

    [Fact]
    public async Task TheRuntime_IsCopiedFromGhcrOnce_AndTheSecondEnsureOnlyAsks()
    {
        string image = PublishRuntime();
        string digest = image[(image.IndexOf('@') + 1)..];

        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);
        int callsAfterTheCopy = _fake.Seen.Count;
        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);

        Assert.True(_fake.Manifests.ContainsKey((RuntimeRepo, digest)));
        Assert.Equal(_fake.GhcrManifests[(GhcrRepo, digest)].Bytes, _fake.Manifests[(RuntimeRepo, digest)].Bytes);
        Assert.Equal(_runtimeLayers.Append(_runtimeConfigDigest).Order(), _fake.Uploaded.Select(u => u.Digest).Order());
        Assert.All(_fake.Uploaded, u => Assert.Equal(RuntimeRepo, u.Repo));
        var second = _fake.Seen.Skip(callsAfterTheCopy).ToList();
        Assert.Equal([(HttpMethod.Head, $"/v2/{RuntimeRepo}/manifests/{digest}")], second.Select(s => (s.Method, s.Uri.AbsolutePath)));
    }

    [Fact]
    public async Task TheGhcrToken_GoesToGhcrOnly_NotToTheHostABlobRedirectsTo()
    {
        await _registry.EnsureRuntimeCopyAsync(Runtime, PublishRuntime(), CancellationToken.None);

        var storage = _fake.Seen.Where(s => s.Uri.Host == "storage.test").ToList();
        var ghcrBlobs = _fake.Seen.Where(s => s.Uri.Host == "ghcr.test" && s.Uri.AbsolutePath.Contains("/blobs/")).ToList();
        Assert.Equal(3, storage.Count);
        Assert.All(storage, s => Assert.Null(s.Authorization));
        Assert.All(ghcrBlobs, s => Assert.Equal($"Bearer {FakeRegistries.Token}", s.Authorization));
        Assert.All(_fake.Seen.Where(s => s.Uri.Host == "registry.test"), s => Assert.Null(s.Authorization));
    }

    [Fact]
    public async Task ARuntimeThatIsAnIndex_IsRefused_AndNothingIsWritten()
    {
        byte[] index = Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"mediaType\":\"application/vnd.oci.image.index.v1+json\",\"manifests\":[]}");
        string digest = FakeRegistries.Sha256(index);
        _fake.GhcrManifests[(GhcrRepo, digest)] = ("application/vnd.oci.image.index.v1+json", index);

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(
            () => _registry.EnsureRuntimeCopyAsync(Runtime, $"ghcr.io/{GhcrRepo}@{digest}", CancellationToken.None));

        Assert.Contains("single-platform", e.Message);
        Assert.Empty(_fake.Manifests);
        Assert.Empty(_fake.Uploaded);
    }

    [Fact]
    public async Task AManifestThatIsNotTheDigestAskedFor_IsRefused()
    {
        string image = PublishRuntime();
        string digest = image[(image.IndexOf('@') + 1)..];
        var (mediaType, bytes) = _fake.GhcrManifests[(GhcrRepo, digest)];
        _fake.GhcrManifests[(GhcrRepo, digest)] = (mediaType, bytes.Append((byte)' ').ToArray());

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(() => _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None));

        Assert.Contains("does not match its digest", e.Message);
    }

    [Theory]
    [InlineData("ghcr.io/openvicloud/titan-function-runtimes/python3.15:latest")]
    [InlineData("docker.io/library/python@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ghcr.io/OpenViCloud/x@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("ghcr.io/openvicloud/x@sha256:00")]
    public async Task ARuntimeImageNotPinnedOnGhcr_IsAnArgumentError(string image)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None));
        Assert.Empty(_fake.Seen);
    }

    [Fact]
    public async Task TheFunctionImage_MountsEveryRuntimeLayer_AndUploadsOnlyTheCodeLayerAndItsConfig()
    {
        string image = PublishRuntime();
        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);
        int uploadsBefore = _fake.Uploaded.Count;
        FunctionCodeLayer layer = CodeLayer();

        string digest = await _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, layer, CancellationToken.None);

        Assert.Equal(_runtimeLayers, _fake.Mounted.Select(m => m.Digest));
        Assert.All(_fake.Mounted, m => Assert.Equal((Repo, RuntimeRepo), (m.Repo, m.From)));
        var uploads = _fake.Uploaded.Skip(uploadsBefore).ToList();
        Assert.Equal(2, uploads.Count);
        Assert.All(uploads, u => Assert.Equal(Repo, u.Repo));
        Assert.Contains(uploads, u => u.Digest == layer.Digest);
        Assert.DoesNotContain(uploads, u => _runtimeLayers.Contains(u.Digest));

        var (mediaType, bytes) = _fake.Manifests[(Repo, VersionId.ToString("D"))];
        Assert.Equal(RegistryFunctionImageRegistry.OciManifest, mediaType);
        Assert.Equal(FakeRegistries.Sha256(bytes), digest);
        JsonNode manifest = JsonNode.Parse(bytes)!;
        var layers = manifest["layers"]!.AsArray();
        Assert.Equal(_runtimeLayers.Append(layer.Digest), layers.Select(l => (string)l!["digest"]!));
        Assert.Equal(RegistryFunctionImageRegistry.OciLayer, (string)layers[^1]!["mediaType"]!);
        Assert.Equal(layer.Bytes, (long)layers[^1]!["size"]!);

        string configDigest = (string)manifest["config"]!["digest"]!;
        Assert.Contains(uploads, u => u.Digest == configDigest);
        byte[] configBytes = _fake.Blobs[configDigest];
        Assert.Equal(configBytes.Length, (int)manifest["config"]!["size"]!);
        Assert.Equal("application/vnd.oci.image.config.v1+json", (string)manifest["config"]!["mediaType"]!);
        JsonNode config = JsonNode.Parse(configBytes)!;
        JsonNode runtimeConfig = JsonNode.Parse(_fake.GhcrBlobs[_runtimeConfigDigest])!;
        var diffIds = config["rootfs"]!["diff_ids"]!.AsArray().Select(d => (string)d!).ToList();
        Assert.Equal(runtimeConfig["rootfs"]!["diff_ids"]!.AsArray().Select(d => (string)d!).Append(layer.DiffId), diffIds);
        Assert.Equal("titan function code", (string)config["history"]!.AsArray()[^1]!["created_by"]!);
        Assert.Equal("1970-01-01T00:00:00Z", (string)config["history"]!.AsArray()[^1]!["created"]!);
        Assert.True(JsonNode.DeepEquals(runtimeConfig["config"], config["config"]), "the adapter's command and env stay as they are");
        Assert.Contains("A=b+c", Encoding.UTF8.GetString(configBytes));
    }

    [Fact]
    public async Task ADockerV2Runtime_GetsADockerCodeLayer()
    {
        string image = PublishRuntime(RegistryFunctionImageRegistry.DockerManifest);
        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);

        await _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, CodeLayer(), CancellationToken.None);

        var (mediaType, bytes) = _fake.Manifests[(Repo, VersionId.ToString("D"))];
        Assert.Equal(RegistryFunctionImageRegistry.DockerManifest, mediaType);
        Assert.Equal(RegistryFunctionImageRegistry.DockerLayer, (string)JsonNode.Parse(bytes)!["layers"]!.AsArray()[^1]!["mediaType"]!);
    }

    [Fact]
    public async Task PushingTheSameCodeTwice_GivesTheSameDigest_AndUploadsNothingTheSecondTime()
    {
        string image = PublishRuntime();
        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);
        FunctionCodeLayer layer = CodeLayer();

        string first = await _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, layer, CancellationToken.None);
        int uploads = _fake.Uploaded.Count;
        string second = await _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, layer, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(uploads, _fake.Uploaded.Count);
    }

    [Fact]
    public async Task AMountTheRegistryAnswersWithAnUpload_Fails_AndNoManifestIsPut()
    {
        string image = PublishRuntime();
        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);
        _fake.MountOpensAnUpload = true;

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(
            () => _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, CodeLayer(), CancellationToken.None));

        Assert.Contains("202", e.Message);
        Assert.DoesNotContain(_fake.Manifests.Keys, k => k.Repo == Repo);
    }

    [Fact]
    public async Task ARuntimeThatIsNotAmd64_IsRefused()
    {
        string image = PublishRuntime(architecture: "arm64");
        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(
            () => _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, CodeLayer(), CancellationToken.None));

        Assert.Contains("linux/amd64", e.Message);
    }

    [Fact]
    public async Task PushingBeforeTheRuntimeIsCopied_Fails()
    {
        string image = PublishRuntime();

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(
            () => _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, CodeLayer(), CancellationToken.None));

        Assert.Contains("answered 404", e.Message);
    }

    [Fact]
    public async Task TheImage_IsFound_ThenDeleted_AndDeletingItAgainIsFine()
    {
        string image = PublishRuntime();
        await _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None);
        string digest = await _registry.PushFunctionImageAsync(Runtime, image, TenantId, FunctionId, VersionId, CodeLayer(), CancellationToken.None);

        Assert.True(await _registry.HasFunctionImageAsync(TenantId, FunctionId, digest, CancellationToken.None));
        await _registry.DeleteFunctionImageAsync(TenantId, FunctionId, digest, CancellationToken.None);
        Assert.False(await _registry.HasFunctionImageAsync(TenantId, FunctionId, digest, CancellationToken.None));
        await _registry.DeleteFunctionImageAsync(TenantId, FunctionId, digest, CancellationToken.None);
    }

    [Fact]
    public async Task ARegistryWithDeleteDisabled_FailsTheDelete()
    {
        _fake.DeleteDisabled = true;

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(
            () => _registry.DeleteFunctionImageAsync(TenantId, FunctionId, "sha256:" + new string('a', 64), CancellationToken.None));

        Assert.Contains("answered 405", e.Message);
    }

    [Fact]
    public void ThePullReference_NamesThePullHostTheRepositoryAndTheDigest()
    {
        string digest = "sha256:" + new string('b', 64);

        Assert.Equal($"localhost:5100/fn/{TenantId:D}/{FunctionId:D}@{digest}", _registry.PullReference(TenantId, FunctionId, digest));
        Assert.Equal("localhost:5100/fn/0a1b2c3d-0000-4000-8000-00000000000a/0a1b2c3d-0000-4000-8000-00000000000f@" + digest,
            _registry.PullReference(TenantId, FunctionId, digest));
        Assert.Throws<ArgumentException>(() => _registry.PullReference(TenantId, FunctionId, "latest"));
    }

    [Fact]
    public async Task AGhcrFailure_NamesTheMethodPathAndStatus_NeverTheTokenOrTheSignedUrl()
    {
        string image = PublishRuntime();
        _fake.Override = r => r.RequestUri!.Host == "storage.test" ? HttpStatusCode.InternalServerError : null;

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(() => _registry.EnsureRuntimeCopyAsync(Runtime, image, CancellationToken.None));

        Assert.StartsWith("GHCR: GET storage.test/blobs/sha256:", e.Message);
        Assert.EndsWith("answered 500", e.Message);
        Assert.DoesNotContain(FakeRegistries.Token, e.Message);
        Assert.DoesNotContain("signed-url-secret", e.Message);
    }

    [Fact]
    public async Task AnUnreachableRegistry_IsAnArtifactFailure()
    {
        var registry = new RegistryFunctionImageRegistry(new ThrowingClients(), Options.Create(new FunctionRegistryOptions
        {
            PushUrl = FakeRegistries.PushUrl,
            PullHost = "localhost:5100",
        }), FakeRegistries.Ghcr);

        var e = await Assert.ThrowsAsync<FunctionArtifactException>(
            () => registry.HasFunctionImageAsync(TenantId, FunctionId, "sha256:" + new string('c', 64), CancellationToken.None));

        Assert.Contains("could not be sent", e.Message);
    }

    private sealed class ThrowingClients : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("connection refused");
    }
}
