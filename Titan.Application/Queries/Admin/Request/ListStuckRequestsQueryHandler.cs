using Microsoft.Extensions.Options;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Queries.Admin.Request.Contracts;

namespace Titan.Application.Queries.Admin.Request;

// Stuck is a query, not a state (titan-ops architecture/resource-lifecycle.md#lost-and-stale): the waiting requests older
// than the threshold, each with the status of the command row its key names.
public class ListStuckRequestsQueryHandler(IPendingRequestReader requests, IOptions<RequestStuckOptions> options)
{
    public async Task<List<StuckRequestQueryResult>> HandleAsync(ListStuckRequestsQuery query, CancellationToken ct = default)
    {
        TimeSpan threshold = TimeSpan.FromMinutes(options.Value.ThresholdMinutes);
        IReadOnlyList<StuckRequest> stuck = await requests.ListStuckAsync(threshold, ct);
        return stuck.Select(StuckRequestQueryResult.From).ToList();
    }
}
