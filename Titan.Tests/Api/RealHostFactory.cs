using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Infrastructure.Persistence;
using Titan.Tests.Infrastructure.Integration;

namespace Titan.Tests.Api;

// The host as Program.cs wires it, on its own migrated Postgres database: no service is replaced
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/testing.md#doubles-only-in-unit-tests). Postgres comes from TestPostgres, as for Category=Integration.
// A test that builds one with `new` calls InitializeAsync first; xUnit does it for a class fixture.
public class RealHostFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly TestPostgres _database = new();
    private readonly Dictionary<string, string?> _configuration;
    private readonly Dictionary<string, string> _settings;

    public RealHostFactory() : this(new Dictionary<string, string?>(), new Dictionary<string, string>())
    {
    }

    /// configuration: read when a service is resolved. settings: read while Program.cs builds the host (CORS origins).
    protected RealHostFactory(Dictionary<string, string?> configuration, Dictionary<string, string> settings)
    {
        _configuration = configuration;
        _settings = settings;
    }

    public async Task InitializeAsync()
    {
        await _database.StartAsync();
        var options = new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_database.GetConnectionString()).Options;
        await using var db = new TitanDbContext(options);
        await db.Database.MigrateAsync();
    }

    private bool _databaseDropped;

    // `await using` reaches this override, an xUnit class fixture reaches it through IAsyncLifetime: both drop the
    // test database.
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (!_databaseDropped)
        {
            _databaseDropped = true;
            await _database.DisposeAsync();
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Titan"] = _database.GetConnectionString(),
            ["Keycloak:Authority"] = "https://keycloak.test/realms/titan",
            ["Keycloak:Audience"] = "titan-api",
        };
        foreach (var (key, value) in _configuration)
        {
            configuration[key] = value;
        }
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }
        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(configuration));
    }
}
