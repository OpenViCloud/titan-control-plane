using Npgsql;

namespace Titan.Tests.Infrastructure.Integration;

/// <summary>
/// The pure half of TestPostgres: which server it may use and which connection string a test gets. No database,
/// so this runs in the unit step; the half that creates and drops databases is measured by the Integration suite.
/// </summary>
public sealed class TestPostgresTests
{
    private const string LocalServer = "Host=127.0.0.1;Port=15432;Username=titan_it;Password=pw;Database=postgres";

    [Theory]
    [InlineData("Host=titan-db-rw.titan-db-prd.svc.cluster.local;Username=u;Password=p")]
    [InlineData("Host=10.0.0.5;Username=u;Password=p")]
    [InlineData("Host=db.example.com;Username=u;Password=p")]
    public void A_server_that_is_not_loopback_is_refused(string server)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => TestPostgres.ForDatabase(server, "titan_it_x"));
        Assert.Contains(TestPostgres.ServerVariable, error.Message);
    }

    [Theory]
    [InlineData("Host=127.0.0.1;Username=u;Password=p")]
    [InlineData("Host=localhost;Username=u;Password=p")]
    [InlineData("Host=::1;Username=u;Password=p")]
    public void A_loopback_server_is_accepted(string server)
    {
        string connectionString = TestPostgres.ForDatabase(server, "titan_it_x");
        Assert.Equal("titan_it_x", new NpgsqlConnectionStringBuilder(connectionString).Database);
    }

    [Fact]
    public void The_test_database_keeps_the_server_and_credentials()
    {
        NpgsqlConnectionStringBuilder built = new(TestPostgres.ForDatabase(LocalServer, "titan_it_abc"));
        Assert.Equal("127.0.0.1", built.Host);
        Assert.Equal(15432, built.Port);
        Assert.Equal("titan_it", built.Username);
        Assert.Equal("pw", built.Password);
        Assert.Equal("titan_it_abc", built.Database);
    }

    [Fact]
    public void Each_test_database_has_its_own_name()
    {
        string first = TestPostgres.NewDatabaseName();
        string second = TestPostgres.NewDatabaseName();
        Assert.NotEqual(first, second);
        Assert.StartsWith("titan_it_", first);
    }
}
