using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.Tenant;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Api;

/// An upload through the host as Program.cs wires it (titan-ops features/ovc-function/design.md#code-storage): the route's
/// own body limit, the multipart read, the real inspector, the Idempotency-Key middleware, and the version list. The
/// function is stored directly; the bearer is an API token of its tenant.
// FullHost — its own CI process, for the reason CatalogContractTests gives.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class FunctionUploadHostTests(FunctionUploadHostTests.FunctionHostFactory factory)
    : IClassFixture<FunctionUploadHostTests.FunctionHostFactory>
{
    private const string FunctionCluster = "fn-test";

    public sealed class FunctionHostFactory() : RealHostFactory(new Dictionary<string, string?>(), Settings());

    // Read while Program.cs builds the host. A function cluster makes the bucket and the registry required; these point
    // at hosts no rejected upload reaches. A valid upload, which writes both, is measured on dev.
    private static Dictionary<string, string> Settings() => new()
    {
        ["FunctionClusters:0:Id"] = FunctionCluster,
        ["FunctionCode:Endpoint"] = "https://s3.function.test",
        ["FunctionCode:Region"] = "auto",
        ["FunctionCode:Bucket"] = "titan-function-test",
        ["FunctionCode:AccessKeyId"] = "key-id",
        ["FunctionCode:SecretAccessKey"] = "secret",
        ["FunctionRegistry:PushUrl"] = "http://registry.function.test:5100",
        ["FunctionRegistry:PullHost"] = "localhost:5100",
    };

    private async Task<(HttpClient Client, Function Function)> ClientWithAFunctionAsync()
    {
        Tenant tenant = Tenant.CreatePending("t-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid());
        tenant.Approve();
        string secret = "ovc_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        ApiToken token = ApiToken.Create(tenant.Id, tenant.Members.Single().KeycloakUserId, "ci", secret, 90, DateTimeOffset.UtcNow);
        Function function = Function.Create(tenant.Id, FunctionCluster, "hello");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
            db.Tenants.Add(tenant);
            db.ApiTokens.Add(token);
            db.Functions.Add(function);
            await db.SaveChangesAsync();
        }
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        return (client, function);
    }

    private static HttpRequestMessage Upload(Guid functionId, byte[] code, string config = """{"runtime":"python3.15","handler":"main.handler"}""")
    {
        var form = new MultipartFormDataContent();
        var configPart = new StringContent(config);
        configPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        form.Add(configPart, "config");
        var codePart = new ByteArrayContent(code);
        codePart.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(codePart, "code", "code.zip");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenant/functions/{functionId}/versions") { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        return request;
    }

    private async Task<int> VersionRowsAsync(Guid functionId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        return await db.Set<FunctionVersion>().CountAsync(v => v.FunctionId == functionId);
    }

    private static async Task<string?> CodeOfAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString();

    [Fact]
    public async Task ACodePartOver50MiB_Is413_FunctionCodeTooLarge_AndNoVersion()
    {
        (HttpClient client, Function function) = await ClientWithAFunctionAsync();

        HttpResponseMessage response = await client.SendAsync(Upload(function.Id, new byte[50 * 1024 * 1024 + 1]));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("FUNCTION_CODE_TOO_LARGE", await CodeOfAsync(response));
        Assert.Equal(0, await VersionRowsAsync(function.Id));
    }

    [Fact]
    public async Task ACodePartTheInspectorRefuses_Is422_FunctionCodeInvalid_AndNoVersion()
    {
        (HttpClient client, Function function) = await ClientWithAFunctionAsync();

        HttpResponseMessage response = await client.SendAsync(Upload(function.Id, "not a zip"u8.ToArray()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("FUNCTION_CODE_INVALID", await CodeOfAsync(response));
        Assert.Equal(0, await VersionRowsAsync(function.Id));
    }

    [Fact]
    public async Task AFormWithoutTheCodePart_Is400()
    {
        (HttpClient client, Function function) = await ClientWithAFunctionAsync();
        var form = new MultipartFormDataContent { { new StringContent("""{"runtime":"go"}"""), "config" } };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/tenant/functions/{function.Id}/versions") { Content = form };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await CodeOfAsync(response));
    }
}
