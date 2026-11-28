using System.Net.Http.Headers;
using FluentValidation.TestHelper;
using Microsoft.AspNetCore.Http;
using Titan.Application.Exceptions;
using Titan.Infrastructure.Endpoints;
using Titan.Infrastructure.Endpoints.Function;

namespace Titan.Tests.Infrastructure;

// The upload endpoint reads the form itself (titan-ops features/ovc-function/design.md#code-storage): the config part
// and the zip, streamed to a file and counted, so its parsing is tested here, without a host.
public sealed class UploadFunctionVersionEndpointTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"titan-function-test-{Guid.NewGuid():N}");

    public UploadFunctionVersionEndpointTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ZipPath => Path.Combine(_dir, "code.zip");

    private static async Task<HttpRequest> FormOf(string? config, byte[]? code, string? extraName = null)
    {
        var form = new MultipartFormDataContent("test-boundary");
        if (config != null)
        {
            var part = new StringContent(config);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            form.Add(part, "config");
        }
        if (code != null)
        {
            var part = new ByteArrayContent(code);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(part, "code", "code.zip");
        }
        if (extraName != null)
        {
            form.Add(new StringContent("x"), extraName);
        }
        var context = new DefaultHttpContext();
        context.Request.ContentType = form.Headers.ContentType!.ToString();
        context.Request.Body = new MemoryStream(await form.ReadAsByteArrayAsync());
        return context.Request;
    }

    [Fact]
    public async Task AFormWithBothParts_GivesTheConfig_AndTheZipOnDisk()
    {
        byte[] code = [1, 2, 3, 4, 5];
        HttpRequest request = await FormOf("""{"runtime":"python3.15","handler":"main.handler","memoryMiB":512}""", code, extraName: "note");

        UploadFunctionVersionConfig config = await UploadFunctionVersionEndpoint.ReadFormAsync(request, ZipPath, default);

        Assert.Equal("python3.15", config.Runtime);
        Assert.Equal("main.handler", config.Handler);
        Assert.Equal(512, config.MemoryMiB);
        Assert.Null(config.TimeoutSeconds);
        Assert.Equal(code, await File.ReadAllBytesAsync(ZipPath));
    }

    [Fact]
    public async Task ACodePartOfExactly50MiB_IsTaken_AndOneByteMore_Is413()
    {
        byte[] limit = new byte[UploadFunctionVersionEndpoint.MaxCodeBytes];
        HttpRequest atTheLimit = await FormOf("""{"runtime":"go"}""", limit);
        await UploadFunctionVersionEndpoint.ReadFormAsync(atTheLimit, ZipPath, default);
        File.Delete(ZipPath);

        byte[] over = new byte[UploadFunctionVersionEndpoint.MaxCodeBytes + 1];
        HttpRequest overTheLimit = await FormOf("""{"runtime":"go"}""", over);
        var e = await Assert.ThrowsAsync<PayloadTooLargeException>(() =>
            UploadFunctionVersionEndpoint.ReadFormAsync(overTheLimit, ZipPath, default));

        Assert.Equal("FUNCTION_CODE_TOO_LARGE", e.Code);
        Assert.Equal(413, ErrorStatus.For(e).Status);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AFormMissingAPart_Is400(bool hasConfig, bool hasCode)
    {
        HttpRequest request = await FormOf(hasConfig ? """{"runtime":"go"}""" : null, hasCode ? [1] : null);

        var e = await Assert.ThrowsAsync<BadRequestException>(() => UploadFunctionVersionEndpoint.ReadFormAsync(request, ZipPath, default));

        Assert.Equal("VALIDATION_ERROR", e.Code);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"handler":"main.handler"}""")]
    [InlineData("""{"runtime":"python3.15","memoryMiB":100}""")]
    [InlineData("""{"runtime":"python3.15","timeoutSeconds":301}""")]
    [InlineData("""{"runtime":"python3.15","handler":"../main"}""")]
    public async Task AConfigThatIsNotOne_Is400(string config)
    {
        HttpRequest request = await FormOf(config, [1]);

        var e = await Assert.ThrowsAsync<BadRequestException>(() => UploadFunctionVersionEndpoint.ReadFormAsync(request, ZipPath, default));

        Assert.Equal("VALIDATION_ERROR", e.Code);
    }

    [Fact]
    public async Task ABodyThatIsNotAForm_Is400()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/zip";
        context.Request.Body = new MemoryStream([1, 2, 3]);

        var e = await Assert.ThrowsAsync<BadRequestException>(() =>
            UploadFunctionVersionEndpoint.ReadFormAsync(context.Request, ZipPath, default));

        Assert.Equal("VALIDATION_ERROR", e.Code);
    }

    [Fact]
    public void TheRouteRaisesItsBodyLimit_AndAsksForAnIdempotencyKey()
    {
        Assert.Equal(51L * 1024 * 1024, UploadFunctionVersionEndpoint.MaxBodyBytes);
        Assert.True(typeof(Titan.Infrastructure.Auth.IRequiresIdempotencyKey).IsAssignableFrom(typeof(UploadFunctionVersionEndpoint)));
    }

    [Theory]
    [InlineData("main.handler", true)]
    [InlineData("pkg.module.handler", true)]
    [InlineData("index.handler", true)]
    [InlineData("main", false)]
    [InlineData("/abs.handler", false)]
    public void TheHandler_IsModuleDotFunction(string handler, bool valid)
    {
        var result = new UploadFunctionVersionConfigValidator().TestValidate(new UploadFunctionVersionConfig("python3.15", handler, null, null));

        Assert.Equal(valid, result.IsValid);
    }
}
