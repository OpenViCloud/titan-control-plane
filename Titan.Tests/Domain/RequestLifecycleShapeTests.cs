using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// titan-ops architecture/resource-lifecycle.md#shape: every aggregate on the request shape has the same four methods, its
// Mark* return nothing, a report with a foreign id changes nothing, every lifecycle handler locks the row, and every
// command payload carries the request id. Reflection and a factory for the first three; source text for the last two.
//
// What this is SILENT about: a lock taken by another name, a lock on another row (create locks its network), a payload built outside ProviderCommandQueue, and the
// semantics of EndRequest beyond "a foreign id is ignored" (VmRequestLifecycleTests holds those).
public class RequestLifecycleShapeTests
{
    private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    // Aggregates on the new shape: the ones with a Pending property (titan-ops architecture/resource-lifecycle.md#rollout).
    private static List<Type> OnTheRequestShape() =>
        typeof(Vm).Assembly.GetExportedTypes()
            .Where(t => t.IsClass && t.Namespace is not null && t.Namespace.StartsWith("Titan.Domain.Aggregates.", StringComparison.Ordinal)
                        && t.GetProperty("Pending", BindingFlags.Public | BindingFlags.Instance) is not null)
            .ToList();

    // Each rollout phase adds its aggregate here, with the factory that builds one waiting on a request.
    private static readonly Dictionary<Type, Func<object>> WaitingOnARequest = new()
    {
        [typeof(Vm)] = () => Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "shape", "m", "tpl", "cluster-a", 20),
    };

    [Fact]
    public void EveryAggregateOnTheShape_HasTheFourMethods_AndIsKnownHere()
    {
        List<Type> shaped = OnTheRequestShape();
        Assert.NotEmpty(shaped);
        Assert.Equal(shaped.OrderBy(t => t.Name), WaitingOnARequest.Keys.OrderBy(t => t.Name));

        var missing = new List<string>();
        foreach (Type type in shaped)
        {
            string[] names = type.GetMethods(Declared).Select(m => m.Name).Distinct().ToArray();
            foreach (string required in new[] { "EndRequest", "MarkObserved", "MarkNotFound" })
            {
                if (!names.Contains(required)) missing.Add($"{type.Name}.{required}");
            }
            if (!names.Any(n => n.StartsWith("Request", StringComparison.Ordinal))) missing.Add($"{type.Name}.Request*");
        }
        Assert.True(missing.Count == 0, "Aggregates on the request shape without the four methods: " + string.Join(", ", missing));
    }

    [Fact] // a Mark* that returns something is a Mark* that ends a request somewhere
    public void MarkMethods_ReturnNothing()
    {
        var offenders = OnTheRequestShape()
            .SelectMany(t => t.GetMethods(Declared).Where(m => m.Name.StartsWith("Mark", StringComparison.Ordinal) && !m.Name.StartsWith("MarkSnapshot", StringComparison.Ordinal)))
            .Where(m => m.ReturnType != typeof(void))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name} returns {m.ReturnType.Name}")
            .ToList();
        Assert.True(offenders.Count == 0, "Mark* must return void (titan-ops architecture/resource-lifecycle.md#shape): " + string.Join(", ", offenders));
    }

    [Fact] // run for real, through each type's factory: a report naming another request leaves every public property as it was
    public void EndRequest_WithAForeignId_ReturnsNull_AndChangesNoPublicProperty()
    {
        foreach ((Type type, Func<object> build) in WaitingOnARequest)
        {
            object aggregate = build();
            MethodInfo endRequest = type.GetMethod("EndRequest", BindingFlags.Public | BindingFlags.Instance)!;
            object end = ADoneReportFor(type);
            Dictionary<string, object?> before = PublicProperties(aggregate);

            object? result = endRequest.Invoke(aggregate, [Guid.NewGuid(), end]);

            Assert.Null(result);
            Assert.Equal(before, PublicProperties(aggregate));
        }
    }

    private static object ADoneReportFor(Type type) => type == typeof(Vm)
        ? VmRequestEnd.Done("foreign", new VmObservation(VmState.Running, "10.0.0.9", null))
        : throw new InvalidOperationException($"no report builder for {type.Name}: add one with its rollout phase");

    private static Dictionary<string, object?> PublicProperties(object aggregate) =>
        aggregate.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && !typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType) || p.PropertyType == typeof(string))
            .ToDictionary(p => p.Name, p => p.GetValue(aggregate));

    // ---------- source text: handlers lock, payloads carry the id ----------

    [Fact]
    public void EveryVmLifecycleHandler_LocksTheRow()
    {
        string[] folders = [Path.Combine("Titan.Application", "Commands", "Compute", "Vm"), Path.Combine("Titan.Application", "Commands", "Admin", "Vm")];
        var handlers = folders
            .SelectMany(f => Directory.EnumerateFiles(Path.Combine(RepoRoot.ControlPlane, f), "*CommandHandler.cs"))
            .Where(f => !Path.GetFileName(f).StartsWith("Rename", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith("RequestVmSsh", StringComparison.Ordinal))
            .ToList();
        Assert.True(handlers.Count >= 8, $"only {handlers.Count} VM lifecycle handlers found — the scan is blind");

        var unlocked = handlers
            .Where(f => !File.ReadAllText(f).Contains("ForUpdateAsync(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.True(unlocked.Count == 0,
            "VM lifecycle handlers that read the VM without its row lock (titan-ops architecture/resource-lifecycle.md#one-key-per-request): "
            + string.Join(", ", unlocked));
    }

    [Fact]
    public void EveryVmCommandPayload_CarriesTheRequestId()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot.ControlPlane, "Titan.Infrastructure", "ProviderCommands", "ProviderCommandQueue.cs"));
        var vmEnqueues = Regex.Matches(src, @"public Task EnqueueVm(Create|Delete|Stop|Start)Async\([\s\S]*?ProviderCommandRerun\.\w+, ct\);")
            .Cast<Match>().ToList();
        Assert.Equal(4, vmEnqueues.Count);

        // A property named requestId, not any mention: stop and start also write `intentId = requestId`.
        var without = vmEnqueues.Where(m => !Regex.IsMatch(m.Value, @"new\s*\{(?:[^}]*,)?\s*requestId\s*[,}]")).Select(m => "EnqueueVm" + m.Groups[1].Value).ToList();
        Assert.True(without.Count == 0, "VM command payloads without requestId (titan-ops architecture/resource-lifecycle.md#callback-routes): " + string.Join(", ", without));
    }
}
