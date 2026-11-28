using FastEndpoints;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.Function;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.FunctionSecret;
using Titan.Domain.Exceptions;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.FunctionSecrets;
using Titan.Tests.Fakes;
using Titan.Tests.Infrastructure.FunctionSecrets;

namespace Titan.Tests.Infrastructure;

// The worker's read of a version's secret values (titan-ops features/ovc-function/design.md#function-secrets): the
// internal token before anything is read, then 404 for a function or version it does not know, 422 for a secret gone,
// and the values under their variables.
public class GetFunctionSecretValuesEndpointTests
{
    private readonly FakeFunctionRepository _functions = new();
    private readonly FakeFunctionSecretRepository _secrets = new();
    private readonly AesGcmFunctionSecretCipher _cipher = AesGcmFunctionSecretCipherTests.Cipher();

    private static IConfiguration ConfigWithToken() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = "secret" })
            .Build();

    private GetFunctionSecretValuesEndpoint Endpoint(Guid functionId, Guid versionId, string? token = "secret") =>
        Factory.Create<GetFunctionSecretValuesEndpoint>(
            ctx =>
            {
                if (token != null)
                {
                    ctx.Request.Headers["X-Internal-Token"] = token;
                }
                ctx.Request.RouteValues["functionId"] = functionId.ToString();
                ctx.Request.RouteValues["versionId"] = versionId.ToString();
            },
            new GetFunctionSecretValuesQueryHandler(_functions, _secrets, _cipher), ConfigWithToken());

    private (Function Function, FunctionVersion Version) AVersionReading(string value)
    {
        Function function = TestFunctions.AFunction();
        _functions.Rows.Add(function);
        FunctionSecretValue encrypted = _cipher.Encrypt(function.TenantId, "DB_PASSWORD", 1, value);
        FunctionSecret secret = FunctionSecret.Create(function.TenantId, "DB_PASSWORD", encrypted, "kc-member");
        _secrets.Saved.Add(secret);
        return (function, function.WithSecrets(FunctionSecretRef.Of("DB", secret.Id, secret.Name, 1)));
    }

    [Fact]
    public void TheRoute_IsTheWorkers_AndAnonymousToTheBearerScheme()
    {
        var ep = Endpoint(Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(["internal/functions/{functionId:guid}/versions/{versionId:guid}/secret-values"], ep.Definition.Routes);
        Assert.NotNull(ep.Definition.AnonymousVerbs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task WithoutTheToken_Is401_AndNothingIsRead(string? token)
    {
        (Function function, FunctionVersion version) = AVersionReading("s3cr3t");
        var ep = Endpoint(function.Id, version.Id, token);

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Null(ep.Response?.Values);
    }

    [Fact]
    public async Task WithTheToken_AnswersTheValues_UnderTheirVariables()
    {
        (Function function, FunctionVersion version) = AVersionReading("s3cr3t");
        var ep = Endpoint(function.Id, version.Id);

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(200, ep.HttpContext.Response.StatusCode);
        Assert.Equal(new Dictionary<string, string> { ["DB"] = "s3cr3t" }, ep.Response.Values);
    }

    [Fact]
    public async Task AnUnknownFunction_OrAVersionOfAnother_Is404()
    {
        (Function function, FunctionVersion version) = AVersionReading("s3cr3t");

        var noFunction = await Assert.ThrowsAsync<NotFoundException>(() => Endpoint(Guid.NewGuid(), version.Id).HandleAsync(CancellationToken.None));
        var noVersion = await Assert.ThrowsAsync<NotFoundException>(() => Endpoint(function.Id, Guid.NewGuid()).HandleAsync(CancellationToken.None));

        Assert.Equal(("FUNCTION_NOT_FOUND", "FUNCTION_VERSION_NOT_FOUND"), (noFunction.Code, noVersion.Code));
    }

    [Fact]
    public async Task ASecretDeletedSinceTheUpload_Is422()
    {
        (Function function, FunctionVersion version) = AVersionReading("s3cr3t");
        _secrets.Saved.Clear();

        var e = await Assert.ThrowsAsync<BusinessException>(() => Endpoint(function.Id, version.Id).HandleAsync(CancellationToken.None));

        Assert.Equal("FUNCTION_SECRET_NOT_FOUND", e.Code);
    }
}
