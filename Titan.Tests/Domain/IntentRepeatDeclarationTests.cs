using Titan.Application.Commands.Admin.Backup;
using System.Reflection;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Admin.Vm;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.VmSnapshot;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Platform.Tenant;
using Titan.Infrastructure.Auth;
using Titan.Tests.Services;
using Xunit.Sdk;

namespace Titan.Tests.Domain;

// https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#new-async-intent-checklist: a new asynchronous intent states what a
// repeat does. Every command handler that takes IProviderCommandQueue has exactly one row below, and the row is
// checked against something outside this table.
//
//   RepeatCheck    — the handler recognises a repeat before calling the aggregate (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat). The row
//                    names a handler-level test by nameof, so deleting or renaming that test is a
//                    compile error, and the guard checks it is a real [Fact]/[Theory].
//   IdempotencyKey — a create: a repeat is replayed by the Idempotency-Key middleware (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#idempotency-key). The
//                    guard checks an endpoint marked IRequiresIdempotencyKey injects this handler.
//   Excluded       — a written reason. Reviewed like any other line of code.
//
// The set of handlers comes from reflection, not from this table, and the check runs both ways: a
// handler with no row is red, and a row for a handler that no longer enqueues a provider command is red.
//
// Silent about:
//   - whether the repeat test tests the right thing: one that asserts nothing passes.
//   - a handler that reaches the provider command queue other than through a constructor parameter (the same blind spot
//     as ReadWriteSeparationTests).
//   - work through the wallet outbox (pay / settle / confirm top-up), which does not take IProviderCommandQueue;
//     its repeat behaviour is pinned by InvoiceServiceTests and TopUpServiceTests.
public class IntentRepeatDeclarationTests
{
    private abstract record RepeatHandling;
    private sealed record RepeatCheck(Type TestClass, string TestMethod) : RepeatHandling;
    private sealed record IdempotencyKey : RepeatHandling;
    private sealed record Excluded(string Reason) : RepeatHandling;

    private static readonly Dictionary<Type, RepeatHandling> Declared = new()
    {
        // ── creates (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#idempotency-key) ──
        [typeof(CreateVmCommandHandler)] = new IdempotencyKey(),
        [typeof(CreateVmSnapshotCommandHandler)] = new IdempotencyKey(),
        [typeof(CreateVolumeCommandHandler)] = new IdempotencyKey(),
        [typeof(CreateVolumeSnapshotCommandHandler)] = new IdempotencyKey(),
        [typeof(RestoreVolumeSnapshotCommandHandler)] = new IdempotencyKey(),
        [typeof(CreateNetworkCommandHandler)] = new IdempotencyKey(),
        [typeof(CreateFirewallRuleCommandHandler)] = new IdempotencyKey(),
        [typeof(CreateLoadBalancerCommandHandler)] = new IdempotencyKey(),

        // ── VM ──
        [typeof(StopVmCommandHandler)] =
            new RepeatCheck(typeof(VmServiceTests), nameof(VmServiceTests.Stop_Repeated_RecordsOneRequest_AndResendsTheSameIntentId)),
        [typeof(StartVmCommandHandler)] =
            new RepeatCheck(typeof(VmServiceTests), nameof(VmServiceTests.Start_Repeated_RecordsOneRequest)),
        [typeof(DeleteVmCommandHandler)] =
            new RepeatCheck(typeof(VmServiceTests), nameof(VmServiceTests.Delete_Repeated_RecordsOneRequest_AndResendsTheSameCommand)),
        [typeof(DeleteAdminVmCommandHandler)] =
            new RepeatCheck(typeof(AdminResourceServiceTests), nameof(AdminResourceServiceTests.DeleteVm_Repeated_AuditsOnce_AndResendsTheCommand)),

        // ── group B: a repeat is a no-op ──
        [typeof(RevertVmSnapshotCommandHandler)] =
            new RepeatCheck(typeof(VmSnapshotServiceTests), nameof(VmSnapshotServiceTests.Revert_WhileReverting_IsANoOp_SavesNothing_EnqueuesNoCommand)),
        [typeof(DeleteVmSnapshotCommandHandler)] =
            new RepeatCheck(typeof(VmSnapshotServiceTests), nameof(VmSnapshotServiceTests.Delete_RepeatedOrDone_IsANoOp_SavesNothing_EnqueuesNoCommand)),
        [typeof(AttachVolumeCommandHandler)] =
            new RepeatCheck(typeof(VolumeServiceTests), nameof(VolumeServiceTests.Attach_RepeatedToTheSameVm_Succeeds_WithoutASecondCommand)),
        [typeof(DetachVolumeCommandHandler)] =
            new RepeatCheck(typeof(VolumeServiceTests), nameof(VolumeServiceTests.Detach_VolumeNotAttached_IsANoOp)),
        [typeof(DeleteVolumeCommandHandler)] =
            new RepeatCheck(typeof(VolumeServiceTests), nameof(VolumeServiceTests.Delete_RepeatedOrDone_IsANoOp_RecordsNoActivity_EnqueuesNoCommand)),
        [typeof(DeleteVolumeSnapshotCommandHandler)] =
            new RepeatCheck(typeof(VolumeSnapshotServiceTests), nameof(VolumeSnapshotServiceTests.Delete_RepeatedOrDone_IsANoOp_EnqueuesNoCommand)),
        [typeof(DeleteNetworkCommandHandler)] =
            new RepeatCheck(typeof(NetworkServiceTests), nameof(NetworkServiceTests.Delete_Repeated_IsANoOp_RecordsNoActivity_EnqueuesNoCommand)),

        // ── firewall ──
        [typeof(DeleteFirewallRuleCommandHandler)] =
            new RepeatCheck(typeof(FirewallServiceTests), nameof(FirewallServiceTests.Delete_Repeated_ChangesNothing)),

        // ── load balancer ──
        [typeof(UpdateLoadBalancerCommandHandler)] =
            new RepeatCheck(typeof(LoadBalancerServiceTests), nameof(LoadBalancerServiceTests.Update_Repeated_WhileItWaits_SendsNothingMore)),
        [typeof(DeleteLoadBalancerCommandHandler)] =
            new RepeatCheck(typeof(LoadBalancerServiceTests), nameof(LoadBalancerServiceTests.Delete_Repeated_OrOfADeletedOne_SendsNothing)),

        // ── catalog ──
        [typeof(SyncTemplatesCommandHandler)] =
            new RepeatCheck(typeof(TemplateServiceTests), nameof(TemplateServiceTests.SyncAll_Repeated_KeepsTheSameRequest_AndResendsTheCommand)),

        // ── backup ──
        [typeof(ScheduleBackupsCommandHandler)] =
            new RepeatCheck(typeof(ScheduleBackupsCommandTests), nameof(ScheduleBackupsCommandTests.RunTwiceTheSameDay_AddsNoRow)),

        // ── excluded ── none today. A handler that genuinely has no repeat to recognise is declared here,
        // with its reason.
    };

    private static readonly Assembly ApplicationAssembly = typeof(CreateVmCommandHandler).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(IRequiresIdempotencyKey).Assembly;

    // The oracle, independent of the table: every command handler whose constructor takes IProviderCommandQueue.
    private static List<Type> CommandEnqueuingHandlers()
    {
        List<Type> handlers = new();
        foreach (Type type in ApplicationAssembly.GetExportedTypes())
        {
            bool isCommandHandler = type.IsClass && !type.IsAbstract && type.Name.EndsWith("CommandHandler", StringComparison.Ordinal);
            if (!isCommandHandler)
            {
                continue;
            }

            bool takesCommandQueue = type.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == typeof(IProviderCommandQueue));
            if (takesCommandQueue)
            {
                handlers.Add(type);
            }
        }
        return handlers;
    }

    [Fact]
    public void EveryCommandEnqueuingHandler_DeclaresWhatARepeatDoes()
    {
        List<Type> handlers = CommandEnqueuingHandlers();
        // An empty scan would pass everything below while checking nothing.
        Assert.NotEmpty(handlers);

        List<string> undeclared = handlers
            .Where(h => !Declared.ContainsKey(h))
            .Select(h => h.FullName!)
            .OrderBy(n => n)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "These handlers enqueue a provider command but do not say what a repeat does (docs/architecture/intent-and-observation.md#new-async-intent-checklist). Add a row to "
            + "IntentRepeatDeclarationTests.Declared — RepeatCheck with its repeat test, IdempotencyKey for a "
            + "create, or Excluded with a reason:\n  " + string.Join("\n  ", undeclared));
    }

    [Fact]
    public void EveryDeclaration_BelongsToAHandlerThatStillEnqueuesACommand()
    {
        HashSet<Type> handlers = CommandEnqueuingHandlers().ToHashSet();
        Assert.NotEmpty(handlers);

        List<string> stale = Declared.Keys
            .Where(h => !handlers.Contains(h))
            .Select(h => h.FullName!)
            .OrderBy(n => n)
            .ToList();

        Assert.True(stale.Count == 0,
            "These rows describe handlers that no longer take IProviderCommandQueue. A row for a dead edge is a lie "
            + "the next reader believes — remove it:\n  " + string.Join("\n  ", stale));
    }

    [Fact]
    public void EveryRepeatCheck_NamesARealTest()
    {
        List<RepeatCheck> checks = Declared.Values.OfType<RepeatCheck>().ToList();
        Assert.NotEmpty(checks);

        List<string> notATest = new();
        foreach (RepeatCheck check in checks)
        {
            MethodInfo? method = check.TestClass.GetMethod(check.TestMethod, BindingFlags.Public | BindingFlags.Instance);
            bool isATest = method != null && method.GetCustomAttribute<FactAttribute>(inherit: true) != null;
            if (!isATest)
            {
                notATest.Add($"{check.TestClass.Name}.{check.TestMethod}");
            }
        }

        Assert.True(notATest.Count == 0,
            "These RepeatCheck rows name a method that is not a [Fact]/[Theory]:\n  " + string.Join("\n  ", notATest));
    }

    [Fact]
    public void EveryIdempotencyKeyRow_IsACreateBehindAKeyRequiredEndpoint()
    {
        List<Type> creates = Declared.Where(d => d.Value is IdempotencyKey).Select(d => d.Key).ToList();
        Assert.NotEmpty(creates);

        List<Type> keyRequiredEndpoints = InfrastructureAssembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IRequiresIdempotencyKey).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(keyRequiredEndpoints);

        List<string> unprotected = new();
        foreach (Type create in creates)
        {
            bool someKeyRequiredEndpointInjectsIt = keyRequiredEndpoints.Any(e => e.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Any(p => p.ParameterType == create));
            if (!someKeyRequiredEndpointInjectsIt)
            {
                unprotected.Add(create.FullName!);
            }
        }

        Assert.True(unprotected.Count == 0,
            "These rows claim the Idempotency-Key replays a repeat, but no endpoint implementing "
            + "IRequiresIdempotencyKey injects the handler — a repeat would create twice:\n  "
            + string.Join("\n  ", unprotected));
    }

    [Fact]
    public void EveryExclusion_SaysWhy()
    {
        List<Excluded> exclusions = Declared.Values.OfType<Excluded>().ToList();

        foreach (Excluded exclusion in exclusions)
        {
            Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason));
        }
    }
}
