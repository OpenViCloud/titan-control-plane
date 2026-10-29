using System.Net;
using System.Text.Json;

namespace Titan.Tests.Api;

// An error declares the length of its body (titan-ops architecture/http-contract.md#error-envelope): the edge in front of
// staging turns a refused websocket upgrade answered with a chunked body into its own 502, so the CLI lost the code.
// Measured through the real host because the error writer is the host's exception handler.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ErrorResponseHostTests
{
    private const string InternalToken = "test-token";

    [Fact]
    public async Task AnError_DeclaresTheByteLengthOfItsBody_AndKeepsTheEnvelope()
    {
        await using var factory = new TokenFactory();
        await factory.InitializeAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/provider-commands/leases/{Guid.NewGuid()}");
        request.Headers.Add("X-Internal-Token", InternalToken);

        HttpResponseMessage response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        Assert.True(response.Content.Headers.Contains("Content-Length"), "the error has no Content-Length header");
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        using JsonDocument envelope = JsonDocument.Parse(body);
        Assert.Equal(404, envelope.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("PROVIDER_COMMAND_LEASE_NOT_FOUND", envelope.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(envelope.RootElement.GetProperty("title").GetString()));
    }

    // What gitops/control-plane/overlays/staging/patch-env.yaml of titan-ops sets on dev and staging.
    private sealed class TokenFactory() : RealHostFactory(
        new Dictionary<string, string?> { ["Internal:Token"] = InternalToken }, new Dictionary<string, string>());
}
