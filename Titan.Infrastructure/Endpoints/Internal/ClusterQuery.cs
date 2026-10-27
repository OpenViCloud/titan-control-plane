using Microsoft.AspNetCore.Http;

namespace Titan.Infrastructure.Endpoints.Internal;

internal static class ClusterQuery
{
    public static string? From(HttpRequest request) => request.Query["clusterId"].FirstOrDefault();
}
