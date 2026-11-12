using Npgsql;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// Up to four control plane replicas share Postgres' max_connections (200 on dev and prd): each replica's pool is capped
// unless the connection string sets its own size (titan-ops architecture/platform-infra.md#control-plane-replicas).
public class TitanConnectionStringTests
{
    [Fact]
    public void WithoutAPoolSize_ThePoolIsCapped()
    {
        string capped = TitanConnectionString.WithPoolCap("Host=db;Database=titan;Username=titan;Password=x");

        Assert.Equal(TitanConnectionString.MaxPoolSize, new NpgsqlConnectionStringBuilder(capped).MaxPoolSize);
        Assert.Equal("db", new NpgsqlConnectionStringBuilder(capped).Host);
    }

    [Theory]
    [InlineData("Host=db;Maximum Pool Size=10")]
    [InlineData("Host=db;maxpoolsize=10")]
    public void APoolSizeTheStringSets_IsKept(string connectionString)
    {
        Assert.Equal(10, new NpgsqlConnectionStringBuilder(TitanConnectionString.WithPoolCap(connectionString)).MaxPoolSize);
    }

    [Fact]
    public void FourReplicasAtTheCap_FitUnderMaxConnections_WithRoomForTheRest()
    {
        Assert.True(4 * TitanConnectionString.MaxPoolSize <= 200 - 40);
    }
}
