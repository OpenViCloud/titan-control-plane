using Microsoft.AspNetCore.Http;

namespace Titan.Infrastructure.Endpoints.Internal;

// A reconciler names its cluster as clusterId; zoneId is still read for a worker that predates it
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#contract-changes).
internal static class ClusterQuery
{
    public static string? From(HttpRequest request)
    {
        string? clusterId = request.Query["clusterId"].FirstOrDefault();
        if (!string.IsNullOrEmpty(clusterId))
        {
            return clusterId;
        }
        return request.Query["zoneId"].FirstOrDefault();
    }
}
