using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Titan.Tests.Api;

// An expected 4xx (the worker's empty lease poll, every few seconds) writes no error log with a stack trace;
// a 500 still does. Measured through the real host because the filter lives in the host's logging setup.
// FullHost — WebApplicationFactory test; own CI process (see CatalogContractTests for why).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ExceptionLoggingHostTests
{
    private const string InternalToken = "test-token";

    [Fact]
    public async Task EmptyLease_Answers404_AndLogsNoError()
    {
        await using var factory = new LoggingFactory();
        await factory.InitializeAsync();

        HttpResponseMessage response = await GetLeaseAsync(factory.CreateClient());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PROVIDER_COMMAND_LEASE_NOT_FOUND", body.RootElement.GetProperty("code").GetString());
        List<CapturedLog> errors = factory.Logs.Where(log => log.Level >= LogLevel.Error).ToList();
        Assert.True(errors.Count == 0,
            "expected no error log, got: " + string.Join(" | ", errors.Select(e => e.Category + ": " + e.Message)));
    }

    // The database is really gone: the connection string names a port nothing listens on. EF wraps Npgsql's error,
    // so the logged exception carries it as an inner exception.
    [Fact]
    public async Task UnexpectedException_Answers500_AndIsStillLoggedWithItsException()
    {
        await using var factory = new NoDatabaseFactory();

        HttpResponseMessage response = await GetLeaseAsync(factory.CreateClient());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        List<CapturedLog> errors = factory.Logs
            .Where(log => log.Level >= LogLevel.Error && Chain(log.Exception).Contains(nameof(NpgsqlException)))
            .ToList();
        Assert.True(errors.Count > 0, "logged: " + string.Join(" | ", factory.Logs.Where(l => l.Level >= LogLevel.Error)
            .Select(l => l.Category + " " + Chain(l.Exception))));
    }

    private static async Task<HttpResponseMessage> GetLeaseAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/internal/provider-commands/leases/{Guid.NewGuid()}");
        request.Headers.Add("X-Internal-Token", InternalToken);
        return await client.SendAsync(request);
    }

    private static string Chain(Exception? exception)
    {
        var names = new List<string>();
        for (Exception? e = exception; e != null; e = e.InnerException)
        {
            names.Add(e.GetType().Name);
        }
        return string.Join(" > ", names);
    }

    public record CapturedLog(string Category, LogLevel Level, string Message, Exception? Exception);

    // What gitops/control-plane/overlays/staging/patch-env.yaml of titan-ops sets on dev and staging.
    private static readonly Dictionary<string, string?> HostConfiguration = new()
    {
        ["Internal:Token"] = InternalToken,
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Information",
    };

    // The capture is a log sink added next to the host's own providers, so the host's filters decide what reaches
    // it; it observes, it replaces nothing.
    private sealed class LoggingFactory() : RealHostFactory(HostConfiguration, new Dictionary<string, string>())
    {
        private readonly ConcurrentQueue<CapturedLog> _logs = new();

        public IReadOnlyCollection<CapturedLog> Logs => _logs.ToArray();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingProvider(_logs)));
        }
    }

    private sealed class NoDatabaseFactory : WebApplicationFactory<Program>
    {
        private readonly ConcurrentQueue<CapturedLog> _logs = new();

        public IReadOnlyCollection<CapturedLog> Logs => _logs.ToArray();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var configuration = new Dictionary<string, string?>(HostConfiguration)
            {
                ["ConnectionStrings:Titan"] = "Host=127.0.0.1;Port=1;Database=titan;Username=titan;Password=none;Timeout=2",
                ["Keycloak:Authority"] = "https://keycloak.test/realms/titan",
                ["Keycloak:Audience"] = "titan-api",
            };
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(configuration));
            builder.ConfigureLogging(logging => logging.AddProvider(new CapturingProvider(_logs)));
        }
    }

    private sealed class CapturingProvider(ConcurrentQueue<CapturedLog> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, logs);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            logs.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception));
        }
    }
}
