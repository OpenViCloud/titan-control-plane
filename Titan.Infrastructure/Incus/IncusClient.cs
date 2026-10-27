using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Incus;

public record IncusServerInfo(string ServerName);

public record IncusImage(string Fingerprint, string Type, string? Description, long Size);

/// A started exec: the operation id and the secret of each of its websockets ("0", "1", "2", "control").
public record IncusExecOperation(string Id, IReadOnlyDictionary<string, string> Secrets);

public sealed class IncusClientFactory(ProviderClientIdentityStore identity)
{
    public async Task<IncusClient> CreateAsync(string endpoint, string serverFingerprint, CancellationToken ct)
    {
        X509Certificate2 clientCertificate = await identity.GetClientCertificateAsync(ct);
        return new IncusClient(endpoint, serverFingerprint, clientCertificate);
    }
}

// Reads the Incus REST API over mutual TLS (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-incus). The server is
// accepted by its pinned SHA-256 fingerprint alone — chain and host name are not checked, because an Incus server
// certificate is self-signed and names the host, not the address Titan dials. Every failure is a ServiceUnavailableException
// whose code tells the admin what to fix.
public sealed class IncusClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _pinnedFingerprint;
    private readonly string _clientFingerprint;
    private readonly X509Certificate2 _clientCertificate;
    private string? _presentedFingerprint;

    public IncusClient(string endpoint, string pinnedFingerprint, X509Certificate2 clientCertificate)
    {
        _endpoint = endpoint;
        _pinnedFingerprint = pinnedFingerprint;
        _clientFingerprint = Convert.ToHexStringLower(SHA256.HashData(clientCertificate.RawData));
        _clientCertificate = clientCertificate;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCertificate },
                // Incus asks for a client certificate without naming acceptable issuers; offer ours regardless.
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate,
                RemoteCertificateValidationCallback = AcceptOnlyThePinnedServer,
            },
        };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private bool AcceptOnlyThePinnedServer(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate == null)
        {
            return false;
        }
        _presentedFingerprint = Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
        return _presentedFingerprint == _pinnedFingerprint;
    }

    /// GET /1.0, refusing anything but auth "trusted": an untrusted client still gets 200 from Incus, with an empty
    /// image list that would read as a healthy, empty catalog.
    public Task<IncusServerInfo> RequireTrustedAsync(CancellationToken ct) =>
        ReadAsync("1.0", server =>
        {
            string auth = server.GetProperty("auth").GetString() ?? "";
            bool isTrusted = auth == "trusted";
            if (!isTrusted)
            {
                throw Untrusted();
            }
            string serverName = server.GetProperty("environment").GetProperty("server_name").GetString() ?? "";
            return new IncusServerInfo(serverName);
        }, ct);

    public Task<bool> IsClusteredAsync(CancellationToken ct) =>
        ReadAsync("1.0/cluster", cluster => cluster.GetProperty("enabled").GetBoolean(), ct);

    /// Member names from the URL list "/1.0/cluster/members/<name>".
    public Task<List<string>> GetClusterMemberNamesAsync(CancellationToken ct) =>
        ReadAsync("1.0/cluster/members", members =>
        {
            var names = new List<string>();
            foreach (JsonElement url in members.EnumerateArray())
            {
                string value = url.GetString() ?? "";
                names.Add(Uri.UnescapeDataString(value[(value.LastIndexOf('/') + 1)..]));
            }
            return names;
        }, ct);

    public Task<List<IncusImage>> GetImagesAsync(CancellationToken ct) =>
        ReadAsync("1.0/images?recursion=1", images =>
        {
            var list = new List<IncusImage>();
            foreach (JsonElement image in images.EnumerateArray())
            {
                list.Add(new IncusImage(
                    image.GetProperty("fingerprint").GetString() ?? "",
                    image.GetProperty("type").GetString() ?? "",
                    DescriptionOf(image),
                    image.GetProperty("size").GetInt64()));
            }
            return list;
        }, ct);

    private static string? DescriptionOf(JsonElement image)
    {
        bool hasProperties = image.TryGetProperty("properties", out JsonElement properties)
            && properties.ValueKind == JsonValueKind.Object;
        if (!hasProperties)
        {
            return null;
        }
        bool hasDescription = properties.TryGetProperty("description", out JsonElement description);
        if (!hasDescription)
        {
            return null;
        }
        return description.GetString();
    }

    /// POST /1.0/instances/{name}/exec, non-interactive and waiting for its websockets: the command starts once all
    /// four are connected. "Instance is not running" is the VM's state; any other refusal is the cluster's
    /// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/vm/design.md#ssh-session).
    public async Task<IncusExecOperation> StartExecAsync(
        string project, string instance, IReadOnlyList<string> command, CancellationToken ct)
    {
        string path = $"1.0/instances/{Uri.EscapeDataString(instance)}/exec?project={Uri.EscapeDataString(project)}";
        var body = new Dictionary<string, object>
        {
            ["command"] = command,
            ["interactive"] = false,
            ["wait-for-websocket"] = true,
            ["record-output"] = false,
        };
        (System.Net.HttpStatusCode status, JsonElement envelope) = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            }, path, ct);
        bool isSuccess = (int)status >= 200 && (int)status < 300;
        if (!isSuccess)
        {
            // Only Incus's own words say the VM is the problem; a bare 404 also comes from a server without the route.
            string error = ErrorOf(envelope);
            bool notRunning = error.Contains("not running", StringComparison.OrdinalIgnoreCase);
            if (notRunning)
            {
                throw new BusinessException("VM_INVALID_STATUS", "The VM is not running on its cluster.");
            }
            throw new ServiceUnavailableException("CLUSTER_ERROR", $"Incus answered {(int)status} on /{path}: {error}");
        }
        try
        {
            JsonElement operation = envelope.GetProperty("metadata");
            string id = operation.GetProperty("id").GetString() ?? throw NotIncusShaped(path);
            var secrets = new Dictionary<string, string>();
            foreach (JsonProperty fd in operation.GetProperty("metadata").GetProperty("fds").EnumerateObject())
            {
                secrets[fd.Name] = fd.Value.GetString() ?? throw NotIncusShaped(path);
            }
            return new IncusExecOperation(id, secrets);
        }
        catch (KeyNotFoundException)
        {
            throw NotIncusShaped(path);
        }
        catch (InvalidOperationException)
        {
            throw NotIncusShaped(path);
        }
    }

    /// One websocket of an operation, with the same client certificate and pinned server as every request. No read
    /// timeout: a shell is idle for as long as its user is; a keep-alive frame every 30 s keeps the path open.
    public async Task<WebSocket> ConnectOperationAsync(string operationId, string secret, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.ClientCertificates = new X509CertificateCollection { _clientCertificate };
        socket.Options.RemoteCertificateValidationCallback = AcceptOnlyThePinnedServer;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        string websocketBase = _endpoint.TrimEnd('/').Replace("https://", "wss://");
        var uri = new Uri($"{websocketBase}/1.0/operations/{Uri.EscapeDataString(operationId)}/websocket?secret={Uri.EscapeDataString(secret)}");
        try
        {
            await socket.ConnectAsync(uri, ct);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// The reason an operation failed, "" while it has none.
    public Task<string> GetOperationErrorAsync(string operationId, CancellationToken ct) =>
        ReadAsync($"1.0/operations/{Uri.EscapeDataString(operationId)}", operation =>
        {
            bool hasError = operation.TryGetProperty("err", out JsonElement err) && err.ValueKind == JsonValueKind.String;
            if (!hasError)
            {
                return "";
            }
            return err.GetString() ?? "";
        }, ct);

    private static string ErrorOf(JsonElement envelope)
    {
        bool hasError = envelope.TryGetProperty("error", out JsonElement message) && message.ValueKind == JsonValueKind.String;
        if (!hasError)
        {
            return "";
        }
        return message.GetString() ?? "";
    }

    // Any answer that is not the Incus shape — missing field, wrong kind, null metadata — is a CLUSTER_ERROR, never a 500.
    private async Task<T> ReadAsync<T>(string path, Func<JsonElement, T> read, CancellationToken ct)
    {
        JsonElement metadata = await GetMetadataAsync(path, ct);
        try
        {
            return read(metadata);
        }
        catch (KeyNotFoundException)
        {
            throw NotIncusShaped(path);
        }
        catch (InvalidOperationException)
        {
            throw NotIncusShaped(path);
        }
        catch (FormatException)
        {
            throw NotIncusShaped(path);
        }
    }

    private async Task<JsonElement> GetMetadataAsync(string path, CancellationToken ct)
    {
        (System.Net.HttpStatusCode status, JsonElement envelope) = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path), path, ct);
        bool isSuccess = (int)status >= 200 && (int)status < 300;
        if (!isSuccess)
        {
            throw new ServiceUnavailableException("CLUSTER_ERROR", $"Incus answered {(int)status} on /{path}: {ErrorOf(envelope)}");
        }
        bool hasMetadata = envelope.TryGetProperty("metadata", out JsonElement metadata);
        if (!hasMetadata)
        {
            throw NotIncusShaped(path);
        }
        return metadata;
    }

    // Transport failure, a refused client and an answer that is not a JSON object end here; any other status is the
    // caller's to read.
    private async Task<(System.Net.HttpStatusCode Status, JsonElement Envelope)> SendAsync(
        Func<HttpRequestMessage> request, string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            using HttpRequestMessage message = request();
            response = await _http.SendAsync(message, ct);
        }
        catch (HttpRequestException e)
        {
            throw TransportFailure(e.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw TransportFailure("timed out");
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw Untrusted();
            }
            JsonElement envelope;
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                envelope = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw NotIncusShaped(path);
            }
            bool isAnObject = envelope.ValueKind == JsonValueKind.Object;
            if (!isAnObject)
            {
                throw NotIncusShaped(path);
            }
            return (response.StatusCode, envelope);
        }
    }

    private ServiceUnavailableException NotIncusShaped(string path) =>
        new("CLUSTER_ERROR", $"{_endpoint}/{path} did not answer in the Incus API shape.");

    private ServiceUnavailableException TransportFailure(string reason)
    {
        bool refusedByThePin = _presentedFingerprint != null && _presentedFingerprint != _pinnedFingerprint;
        if (refusedByThePin)
        {
            return new ServiceUnavailableException("CLUSTER_FINGERPRINT_MISMATCH",
                $"The server at {_endpoint} presented certificate fingerprint {_presentedFingerprint}, " +
                $"not the pinned {_pinnedFingerprint}.");
        }
        return new ServiceUnavailableException("CLUSTER_UNREACHABLE", $"Could not reach {_endpoint}: {reason}");
    }

    private ServiceUnavailableException Untrusted() =>
        new("CLUSTER_UNTRUSTED",
            $"The cluster at {_endpoint} does not trust Titan: add Titan's client certificate " +
            $"(fingerprint {_clientFingerprint}) to its trust store.");

    // The client certificate is shared (ProviderClientIdentityStore): only the HTTP client is ours to dispose.
    public void Dispose() => _http.Dispose();
}
