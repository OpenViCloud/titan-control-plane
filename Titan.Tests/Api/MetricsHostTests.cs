using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Titan.Tests.Api;

// Request counts and durations for the operator metrics tier. The host starts its metric server on a real socket, so
// these tests scrape it over TCP on a free port, and call the API through the in-memory test server.
// prometheus-net keeps one process-wide registry: a line asserted here may also come from another test's request.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class MetricsHostTests
{
    private const string InternalToken = "test-token";
    private const string LeaseRoute = "endpoint=\"internal/provider-commands/leases/{leaseId:guid}\"";

    [Fact]
    public async Task AnsweredRequest_IsCounted_WithItsCodeAndRoute()
    {
        int port = FreePort();
        await using var factory = new DatabaseFactory(port);
        await factory.InitializeAsync();

        HttpResponseMessage lease = await GetLeaseAsync(factory.CreateClient());
        Assert.Equal(HttpStatusCode.NotFound, lease.StatusCode);

        IEnumerable<string> counts = Lines(await ScrapeAsync(port), "http_requests_received_total{");
        Assert.True(counts.Any(line => line.Contains("code=\"404\"") && line.Contains(LeaseRoute) && line.Contains("controller=\"\",action=\"\"")),
            string.Join("\n", counts));
    }

    // The exception handler turns the failure into a 500 after the endpoint has thrown; the count must carry that
    // final code and the route, or a 5xx count misses unhandled exceptions and loses their route.
    [Fact]
    public async Task FailedRequest_IsCounted_As500_WithItsRoute()
    {
        int port = FreePort();
        await using var factory = new NoDatabaseFactory(port);

        HttpResponseMessage lease = await GetLeaseAsync(factory.CreateClient());
        Assert.Equal(HttpStatusCode.InternalServerError, lease.StatusCode);

        IEnumerable<string> counts = Lines(await ScrapeAsync(port), "http_requests_received_total{");
        Assert.True(counts.Any(line => line.Contains("code=\"500\"") && line.Contains(LeaseRoute)), string.Join("\n", counts));
    }

    [Fact]
    public async Task Metrics_CarryARequestDurationHistogram()
    {
        int port = FreePort();
        await using var factory = new DatabaseFactory(port);
        await factory.InitializeAsync();
        await GetLeaseAsync(factory.CreateClient());

        IEnumerable<string> buckets = Lines(await ScrapeAsync(port), "http_request_duration_seconds_bucket{");

        Assert.True(buckets.Any(line => line.Contains(LeaseRoute)), string.Join("\n", buckets.Take(5)));
    }

    // A path no route matches is counted under an empty endpoint, so random paths do not grow the series count.
    [Fact]
    public async Task UnmatchedPath_IsCountedUnderAnEmptyEndpoint()
    {
        int port = FreePort();
        await using var factory = new DatabaseFactory(port);
        await factory.InitializeAsync();
        string path = $"no-such-route-{Guid.NewGuid()}";
        await factory.CreateClient().GetAsync("/" + path);

        string metrics = await ScrapeAsync(port);

        Assert.DoesNotContain(path, metrics);
        IEnumerable<string> counts = Lines(metrics, "http_requests_received_total{");
        Assert.True(counts.Any(line => line.Contains("code=\"404\"") && line.Contains("endpoint=\"\"")), string.Join("\n", counts));
    }

    // The main listener has no /metrics route, even for a request whose Host names the metrics port.
    [Fact]
    public async Task MainListener_DoesNotServeMetrics_WhateverTheHost()
    {
        int port = FreePort();
        await using var factory = new DatabaseFactory(port);
        await factory.InitializeAsync();
        HttpClient client = factory.CreateClient();

        HttpResponseMessage plain = await client.GetAsync("/metrics");
        var spoofed = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        spoofed.Headers.Host = $"localhost:{port}";
        HttpResponseMessage withMetricsHost = await client.SendAsync(spoofed);

        Assert.Equal(HttpStatusCode.NotFound, plain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withMetricsHost.StatusCode);
    }

    // No Authorization header: the scraper holds no token.
    [Fact]
    public async Task MetricServer_NeedsNoToken()
    {
        int port = FreePort();
        await using var factory = new DatabaseFactory(port);
        await factory.InitializeAsync();
        factory.CreateClient();

        using var scraper = new HttpClient();
        HttpResponseMessage response = await scraper.GetAsync($"http://127.0.0.1:{port}/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<string> ScrapeAsync(int port)
    {
        using var scraper = new HttpClient();
        HttpResponseMessage response = await scraper.GetAsync($"http://127.0.0.1:{port}/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static IEnumerable<string> Lines(string metrics, string prefix) =>
        metrics.Split('\n').Where(line => line.StartsWith(prefix));

    private static async Task<HttpResponseMessage> GetLeaseAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/provider-commands/leases/{Guid.NewGuid()}");
        request.Headers.Add("X-Internal-Token", InternalToken);
        return await client.SendAsync(request);
    }

    private sealed class DatabaseFactory(int metricsPort) : RealHostFactory(
        new Dictionary<string, string?> { ["Internal:Token"] = InternalToken },
        new Dictionary<string, string> { ["Metrics:Port"] = metricsPort.ToString() })
    {
    }

    // The database is gone (nothing listens on port 1), so the lease route throws and the host answers 500.
    private sealed class NoDatabaseFactory(int metricsPort) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var configuration = new Dictionary<string, string?>
            {
                ["Internal:Token"] = InternalToken,
                ["ConnectionStrings:Titan"] = "Host=127.0.0.1;Port=1;Database=titan;Username=titan;Password=none;Timeout=2",
                ["Keycloak:Authority"] = "https://keycloak.test/realms/titan",
                ["Keycloak:Audience"] = "titan-api",
                ["Idempotency:HashKey"] = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff",
            };
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(configuration));
            builder.UseSetting("Metrics:Port", metricsPort.ToString());
        }
    }
}
