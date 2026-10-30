using Npgsql;
using Testcontainers.PostgreSql;

namespace Titan.Tests.Infrastructure.Integration;

/// <summary>
/// One empty Postgres database for whoever owns it (a test class instance — xUnit makes one per test — or a
/// collection fixture), with the shape of the Testcontainers container it replaces (StartAsync /
/// GetConnectionString / DisposeAsync).
/// - TITAN_IT_POSTGRES set: a fresh database on that server, dropped afterwards. This is how the Mac runs the
///   suite without Docker, against the dev cluster's Postgres (test-all.sh sets it).
/// - Unset: a throwaway postgres:16 container, as CI has always done.
/// The server must be loopback: the variable reaching a shared database would let tests create and drop there.
/// </summary>
public sealed class TestPostgres : IAsyncDisposable
{
    public const string ServerVariable = "TITAN_IT_POSTGRES";

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "localhost", "::1"];

    private readonly string? _server = Environment.GetEnvironmentVariable(ServerVariable);
    private PostgreSqlContainer? _container;
    private string? _databaseName;
    private string? _connectionString;

    public async Task StartAsync()
    {
        bool useContainer = string.IsNullOrWhiteSpace(_server);
        if (useContainer)
        {
            _container = new PostgreSqlBuilder("postgres:16").Build();
            await _container.StartAsync();
            _connectionString = _container.GetConnectionString();
            return;
        }

        string databaseName = NewDatabaseName();
        string adminConnectionString = ForDatabase(_server!, "postgres");
        await using (NpgsqlConnection admin = new(adminConnectionString))
        {
            await admin.OpenAsync();
            await using NpgsqlCommand create = new($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        _databaseName = databaseName;
        _connectionString = ForDatabase(_server!, databaseName);
    }

    public string GetConnectionString()
    {
        if (_connectionString is null)
        {
            throw new InvalidOperationException("TestPostgres.StartAsync has not run.");
        }
        return _connectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }
        if (_databaseName is null)
        {
            return;
        }

        // Clearing the pool sends each connection's Terminate, which reaches its backend about when the DROP below does.
        // A plain DROP waits up to 5 s for those backends to leave. No FORCE: it kills a backend with its Terminate unread,
        // the kernel resets that socket, and one reset makes kubectl port-forward exit, which ends the tunnel for every test
        // (https://github.com/OpenViCloud/titan-local-cluster/blob/main/docs/topology.md#runbook, TEST-ALL-TUNNEL).
        await using (NpgsqlConnection testConnection = new(GetConnectionString()))
        {
            NpgsqlConnection.ClearPool(testConnection);
        }
        await using NpgsqlConnection admin = new(ForDatabase(_server!, "postgres"));
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE IF EXISTS \"{_databaseName}\"", admin);
        await drop.ExecuteNonQueryAsync();
    }

    public static string NewDatabaseName() => $"titan_it_{Guid.NewGuid():N}";

    public static string ForDatabase(string server, string database)
    {
        NpgsqlConnectionStringBuilder builder = new(server);
        bool isLoopback = LoopbackHosts.Contains(builder.Host);
        if (!isLoopback)
        {
            throw new InvalidOperationException(
                $"{ServerVariable} points at '{builder.Host}'. Integration tests create and drop databases; "
                + "only a loopback server (the dev cluster through port-forward) is allowed.");
        }
        builder.Database = database;
        return builder.ConnectionString;
    }
}
