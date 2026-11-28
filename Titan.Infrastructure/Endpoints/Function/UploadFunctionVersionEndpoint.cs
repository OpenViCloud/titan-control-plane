using System.Text;
using System.Text.Json;
using FastEndpoints;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using MultipartSection = Microsoft.AspNetCore.WebUtilities.MultipartSection;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Application.Exceptions;
using Titan.Infrastructure.Auth;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Infrastructure.Endpoints.Function;

// One upload, multipart/form-data with two parts (titan-ops features/ovc-function/design.md#code-storage): `config`, the
// JSON of UploadFunctionVersionConfig, and `code`, the zip. The form is not bound: the zip is streamed to a file of the
// upload's own temporary directory, never held in memory, and counted as it comes, so a part over the limit stops the
// read. The directory goes when the request ends, whatever the outcome.
public class UploadFunctionVersionEndpoint(UploadFunctionVersionCommandHandler handler)
    : EndpointWithoutRequest<UploadFunctionVersionCommandResult>, IRequiresIdempotencyKey
{
    /// The zip's own limit (titan-ops features/ovc-function/design.md#code-limits).
    public const long MaxCodeBytes = 50L * 1024 * 1024;
    /// The whole body: the zip, the config part and the multipart framing.
    public const long MaxBodyBytes = 51L * 1024 * 1024;
    public const string TooLargeCode = "FUNCTION_CODE_TOO_LARGE";
    // The config is a few fields; a larger part is not one.
    private const int MaxConfigBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override void Configure()
    {
        Post("v1/tenant/functions/{functionId:guid}/versions");
        AllowFileUploads(dontAutoBindFormData: true);
        Options(b => b.WithMetadata(new RequestBodyLimit(MaxBodyBytes, TooLargeCode)));
        Description(b => b.ClearDefaultProduces(200).Produces<UploadFunctionVersionCommandResult>(201, "application/json"));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        Guid functionId = Route<Guid>("functionId");
        string workDir = Path.Combine(Path.GetTempPath(), $"titan-function-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            string zipPath = Path.Combine(workDir, "code.zip");
            UploadFunctionVersionConfig config = await ReadFormAsync(HttpContext.Request, zipPath, ct);
            var result = await handler.HandleAsync(new UploadFunctionVersionCommand(
                functionId,
                config.Runtime!,
                config.Handler,
                config.MemoryMiB ?? FunctionAggregate.DefaultMemoryMiB,
                config.TimeoutSeconds ?? FunctionAggregate.DefaultTimeoutSeconds,
                zipPath,
                workDir), ct);
            await SendAsync(result, 201, ct);
        }
        finally
        {
            DeleteQuietly(workDir);
        }
    }

    /// Reads the two parts, the zip into zipPath. A part missing, given twice, or a config that is not valid JSON of the
    /// right shape is a 400; a zip over MaxCodeBytes a 413, as soon as its byte MaxCodeBytes + 1 arrives.
    internal static async Task<UploadFunctionVersionConfig> ReadFormAsync(HttpRequest request, string zipPath, CancellationToken ct)
    {
        string boundary = BoundaryOf(request.ContentType);
        var reader = new MultipartReader(boundary, request.Body);
        UploadFunctionVersionConfig? config = null;
        bool hasCode = false;

        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(ct)) != null)
        {
            // Clients quote the name (name="config"); the header keeps the quotes.
            ContentDispositionHeaderValue? disposition = section.GetContentDispositionHeader();
            string? name = disposition == null ? null : HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            switch (name)
            {
                case "config":
                    if (config != null) throw Invalid("The form has two config parts.");
                    config = ParseConfig(await ReadTextAsync(section.Body, ct));
                    break;
                case "code":
                    if (hasCode) throw Invalid("The form has two code parts.");
                    await CopyCountingAsync(section.Body, zipPath, ct);
                    hasCode = true;
                    break;
                default:
                    // An extra part is not this upload's: read past it.
                    await section.Body.CopyToAsync(Stream.Null, ct);
                    break;
            }
        }

        if (config == null) throw Invalid("The form has no config part.");
        if (!hasCode) throw Invalid("The form has no code part.");
        return config;
    }

    private static string BoundaryOf(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue? mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("The body must be multipart/form-data with a config part and a code part.");
        }
        string boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value ?? "";
        if (boundary.Length == 0)
        {
            throw Invalid("The multipart body names no boundary.");
        }
        return boundary;
    }

    private static UploadFunctionVersionConfig ParseConfig(string json)
    {
        UploadFunctionVersionConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<UploadFunctionVersionConfig>(json, Json);
        }
        catch (JsonException)
        {
            throw Invalid("The config part is not valid JSON.");
        }
        if (config == null) throw Invalid("The config part is empty.");

        ValidationResult result = new UploadFunctionVersionConfigValidator().Validate(config);
        if (!result.IsValid)
        {
            throw Invalid(string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
        }
        return config;
    }

    private static async Task<string> ReadTextAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxConfigBytes)
            {
                throw Invalid($"The config part is over {MaxConfigBytes} bytes.");
            }
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // The size a zip declares can lie; the bytes that arrive cannot.
    private static async Task CopyCountingAsync(Stream body, string path, CancellationToken ct)
    {
        await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        byte[] chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > MaxCodeBytes)
            {
                throw new PayloadTooLargeException(TooLargeCode, $"The code part is over {MaxCodeBytes} bytes (50 MiB).");
            }
            await file.WriteAsync(chunk.AsMemory(0, read), ct);
        }
    }

    private static BadRequestException Invalid(string message) => new("VALIDATION_ERROR", message);

    private static void DeleteQuietly(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory left behind is the OS's to clear; the answer the customer gets does not depend on it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// The config part of the form (titan-ops features/ovc-function/design.md#function-config). Memory and timeout absent ⇒
// the defaults of Function; handler absent for Go.
public record UploadFunctionVersionConfig(string? Runtime, string? Handler, int? MemoryMiB, int? TimeoutSeconds);
