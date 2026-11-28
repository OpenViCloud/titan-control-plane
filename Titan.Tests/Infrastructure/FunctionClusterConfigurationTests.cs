using Titan.Application.Options;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Infrastructure;

// A function cluster is a config list of its own, FunctionClusters__N__{Id,Name}, apart from the Incus clusters: its
// worker runs inside it, so there is no endpoint and no fingerprint (titan-ops features/ovc-function/design.md#function-cluster-ops).
// Checked once at startup, like ClusterOptions.
public class FunctionClusterConfigurationTests
{
    private static readonly ClusterOptions IncusClusters = new()
    {
        Clusters = [new ClusterConfig { Id = "singapore01", Name = "Singapore 01" }],
    };

    private static FunctionClusterConfig Fn(string id, string name = "") => new() { Id = id, Name = name };

    [Fact]
    public void NoFunctionClusterConfigured_IsAnEmptyList()
    {
        Assert.Empty(FunctionClusterOptions.FromConfiguration(null, IncusClusters).Clusters);
        Assert.Empty(FunctionClusterOptions.FromConfiguration([], IncusClusters).Clusters);
    }

    [Fact]
    public void AFunctionCluster_IsKept_Trimmed_AndNamedAfterItsIdWhenNoNameIsGiven()
    {
        FunctionClusterOptions named = FunctionClusterOptions.FromConfiguration([Fn(" fn-dev ", " Function dev ")], IncusClusters);
        FunctionClusterOptions unnamed = FunctionClusterOptions.FromConfiguration([Fn("fn-two")], IncusClusters);

        Assert.Equal([("fn-dev", "Function dev")], named.Clusters.Select(c => (c.Id, c.Name)));
        Assert.Equal([("fn-two", "fn-two")], unnamed.Clusters.Select(c => (c.Id, c.Name)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankId_StopsTheHost_NamingTheKey(string id)
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            FunctionClusterOptions.FromConfiguration([Fn("fn-dev"), Fn(id)], IncusClusters));

        Assert.Contains("FunctionClusters:1:Id", e.Message);
    }

    [Fact]
    public void AnIdConfiguredTwice_StopsTheHost_NamingTheSecondKey()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            FunctionClusterOptions.FromConfiguration([Fn("fn-dev"), Fn("fn-two"), Fn(" fn-dev")], IncusClusters));

        Assert.Contains("FunctionClusters:2:Id", e.Message);
        Assert.Contains("fn-dev", e.Message);
    }

    // A worker leases by kind, not by cluster: with two function clusters one cluster's worker would lease, and fail for
    // good, the other's sync (titan-ops features/ovc-function/design.md#function-cluster-ops).
    [Fact]
    public void ASecondFunctionCluster_StopsTheHost_NamingTheKey()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            FunctionClusterOptions.FromConfiguration([Fn("fn-dev"), Fn("fn-two")], IncusClusters));

        Assert.Contains("FunctionClusters:1:Id", e.Message);
        Assert.Contains("one function cluster", e.Message);
    }

    // A command's cluster_id names the cluster whose worker runs it: an id shared with an Incus cluster would name two.
    [Fact]
    public void TheIdOfAnIncusCluster_StopsTheHost_NamingTheKey()
    {
        var e = Assert.Throws<InvalidOperationException>(() =>
            FunctionClusterOptions.FromConfiguration([Fn("fn-dev"), Fn("singapore01")], IncusClusters));

        Assert.Contains("FunctionClusters:1:Id", e.Message);
        Assert.Contains("singapore01", e.Message);
    }

    [Fact]
    public void ClusterOf_AFunctionCluster_IsIt()
    {
        FunctionClusterOptions options = FunctionClusterOptions.FromConfiguration([Fn("fn-dev", "Function dev")], IncusClusters);

        Assert.Equal("Function dev", options.ClusterOf("fn-dev").Name);
    }

    [Theory]
    [InlineData("fn-unknown")]
    [InlineData("singapore01")] // an Incus cluster is not a function cluster
    public void ClusterOf_AnyOtherId_IsClusterNotFound(string clusterId)
    {
        FunctionClusterOptions options = FunctionClusterOptions.FromConfiguration([Fn("fn-dev")], IncusClusters);

        var e = Assert.Throws<NotFoundException>(() => options.ClusterOf(clusterId));
        Assert.Equal("CLUSTER_NOT_FOUND", e.Code);
    }
}
