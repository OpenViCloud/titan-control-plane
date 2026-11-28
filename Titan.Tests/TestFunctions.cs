using Titan.Domain.Aggregates.Function;
using FunctionAggregate = Titan.Domain.Aggregates.Function.Function;

namespace Titan.Tests;

// Functions built through the real factory and moves, as TestAggregates does for the other types: a version is added
// through AddVersion, a running one through Deploy and the report of its command.
internal static class TestFunctions
{
    internal const string Cluster = "fn-dev";
    internal static readonly Guid RuntimeId = Guid.Parse("f0de0000-0000-4000-8000-000000000001");
    internal const string RuntimeName = "python3.15";
    internal const string RuntimeImage =
        "ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:c2c3c3e141ea062fa0cb4c11119c8bf45184d98a22f7f384456fd4082883b8d1";
    internal const string Digest = "sha256:" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    internal static FunctionAggregate AFunction(Guid? tenantId = null, string clusterId = Cluster, string name = "hello") =>
        FunctionAggregate.Create(tenantId ?? Guid.NewGuid(), clusterId, name);

    internal static FunctionVersionSpec Spec(FunctionAggregate function, Guid? versionId = null, string? handler = "main.handler",
        int memoryMiB = 256, int timeoutSeconds = 30, Guid? runtimeId = null, string runtimeName = RuntimeName,
        string runtimeImage = RuntimeImage, string digest = Digest, IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyList<FunctionSecretRef>? secretRefs = null, string? schedule = null)
    {
        Guid id = versionId ?? Guid.NewGuid();
        return new FunctionVersionSpec(
            id, runtimeId ?? RuntimeId, runtimeName, runtimeImage, handler, memoryMiB, timeoutSeconds,
            FunctionAggregate.CodeKeyFor(function.TenantId, function.Id, id), "sha256:" + new string('c', 64),
            1234, 5678, 3, digest, $"localhost:5100/fn/{function.TenantId}/{function.Id}@{digest}",
            env ?? new Dictionary<string, string>(), secretRefs ?? [], schedule);
    }

    internal static FunctionVersion WithVersion(this FunctionAggregate function, string? handler = "main.handler", int memoryMiB = 256) =>
        function.AddVersion(Spec(function, handler: handler, memoryMiB: memoryMiB));

    /// A version that reads the given secret versions, each under the variable named in the ref.
    internal static FunctionVersion WithSecrets(this FunctionAggregate function, params FunctionSecretRef[] refs) =>
        function.AddVersion(Spec(function, secretRefs: refs));

    /// The version runs: deployed, and the deploy's command reported Done with the Service the worker named.
    internal static FunctionVersion Running(this FunctionAggregate function, FunctionVersion? version = null)
    {
        version ??= function.WithVersion();
        Guid operationId = function.Deploy(version.Id);
        function.DeploySucceeded(operationId, ServiceOf(function));
        return version;
    }

    internal static string ServiceOf(FunctionAggregate function) => "fn" + function.Id.ToString("N")[..24];
}
