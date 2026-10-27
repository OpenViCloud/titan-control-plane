using System.Diagnostics;
using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Incus;

// Probes an endpoint and fingerprint: GET /1.0 must come back "trusted" from the pinned server.
// A failure is a result, never an exception (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/cluster/design.md#cluster-incus).
public sealed class IncusClusterConnectionTester(IncusClientFactory factory) : IClusterConnectionTester
{
    public async Task<ClusterTestResult> TestAsync(string endpoint, string serverFingerprint, CancellationToken ct)
    {
        string normalizedEndpoint;
        string normalizedFingerprint;
        try
        {
            // The configuration's own rules decide what a valid endpoint and fingerprint are.
            normalizedEndpoint = ZoneConfig.NormalizeEndpoint(endpoint);
            normalizedFingerprint = ZoneConfig.NormalizeFingerprint(serverFingerprint);
        }
        catch (ArgumentException e)
        {
            return new ClusterTestResult(false, null, e.Message);
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using IncusClient client = await factory.CreateAsync(normalizedEndpoint, normalizedFingerprint, ct);
            await client.RequireTrustedAsync(ct);
            return new ClusterTestResult(true, watch.ElapsedMilliseconds, null);
        }
        catch (ServiceUnavailableException e)
        {
            return new ClusterTestResult(false, null, e.Message);
        }
    }
}
