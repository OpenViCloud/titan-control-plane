using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Titan.Infrastructure.Endpoints;

// Every error body declares its length (titan-ops architecture/http-contract.md#error-envelope). The framework JSON writer sends it
// chunked, and the edge in front of staging answers a refused websocket upgrade with a chunked body by its own 502.
public static class JsonErrorWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(HttpResponse response, int status, object body, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength = bytes.Length;
        await response.Body.WriteAsync(bytes, ct);
    }
}
