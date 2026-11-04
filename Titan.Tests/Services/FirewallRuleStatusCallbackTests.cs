using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The old firewall rule callback (titan-ops architecture/resource-lifecycle.md#callback-routes): four reports, the Incus
// ACL rule state word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim), each mapped
// onto the request the rule waits on. The rule is reached through the network the route names, locked
// (titan-ops architecture/code-structure.md#child-entity). The new routes are in NetworkRequestHandlerTests.
public class FirewallRuleStatusCallbackTests
{
    private readonly Network _network = ActiveNetwork();
    private readonly Mock<INetworkRepository> _repo = new();

    public FirewallRuleStatusCallbackTests() =>
        _repo.Setup(r => r.FindByIdForUpdateAsync(_network.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_network);

    private static Network ActiveNetwork()
    {
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "net-1", "cluster-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    private UpdateFirewallRuleStatusCommandHandler Handler(IActivityRecorder activity) => new(_repo.Object, activity);

    private UpdateFirewallRuleStatusCommandHandler Handler() => Handler(Mock.Of<IActivityRecorder>());

    private UpdateFirewallRuleStatusCommand Report(FirewallRule rule, string report, bool? exists = null,
        string? state = null, string? externalRuleId = null, string? reason = null, string? detail = null) =>
        new(_network.Id, rule.Id, report, exists, state, externalRuleId, reason, detail);

    private FirewallRule CreatingRule() =>
        _network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");

    private FirewallRule DeletingRule()
    {
        FirewallRule rule = CreatingRule();
        rule.MarkActive("rule-7");
        _network.DeleteFirewallRule(rule.Id);
        return rule;
    }

    [Fact]
    public async Task Deleted_EndsTheWaitingDeleteDone_AndRecordsTheOutcomeOnce()
    {
        FirewallRule rule = DeletingRule();
        Mock<IActivityRecorder> activity = new();
        UpdateFirewallRuleStatusCommandHandler handler = Handler(activity.Object);

        await handler.HandleAsync(Report(rule, "Deleted"));
        await handler.HandleAsync(Report(rule, "Deleted"));

        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Null(rule.Pending);
        Assert.Equal("rule-7", rule.ExternalId);
        activity.Verify(a => a.StageOutcome(rule.TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
        _repo.Verify(r => r.FindByIdForUpdateAsync(_network.Id, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // Only a delete's own report ends a rule: with no delete waiting, Deleted is Incus no longer having it.
    [Fact]
    public async Task Deleted_WithNoDeleteWaiting_IsNotFound_AndRecordsNothing()
    {
        FirewallRule rule = CreatingRule();
        rule.MarkActive("rule-7");
        Mock<IActivityRecorder> activity = new();

        await Handler(activity.Object).HandleAsync(Report(rule, "Deleted"));

        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        activity.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Created_EndsTheCreateDone_StoresTheProviderId_AndRecordsCreatedOnce()
    {
        FirewallRule rule = CreatingRule();
        Mock<IActivityRecorder> activity = new();

        await Handler(activity.Object).HandleAsync(Report(rule, "Created", externalRuleId: "rule-9"));

        Assert.Equal("rule-9", rule.ExternalId);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
        Assert.Null(rule.Pending);
        activity.Verify(a => a.StageOutcome(rule.TenantId, "firewall_rule.created", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
    }

    [Fact] // the reconciler sees the rule after the create command reported its id: current only, no second outcome
    public async Task ObservedAfterCreated_WritesTheState_AndRecordsNoSecondOutcome()
    {
        FirewallRule rule = CreatingRule();
        Mock<IActivityRecorder> activity = new();
        UpdateFirewallRuleStatusCommandHandler handler = Handler(activity.Object);

        await handler.HandleAsync(Report(rule, "Created", externalRuleId: "rule-9"));
        await handler.HandleAsync(Report(rule, "Observed", exists: true, state: "enabled"));
        await handler.HandleAsync(Report(rule, "Observed", exists: true, state: "enabled"));

        Assert.Equal(FirewallRuleState.enabled, rule.CurrentState);
        Assert.Null(rule.Transition);
        activity.Verify(a => a.StageOutcome(rule.TenantId, "firewall_rule.created", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
        activity.VerifyNoOtherCalls();
    }

    // An observation ends no request (titan-ops architecture/resource-lifecycle.md#invariants): a waiting create stays
    // waiting even when the reconciler reads the rule enabled.
    [Fact]
    public async Task Observed_WhileTheCreateWaits_WritesTheState_AndTheCreateKeepsWaiting()
    {
        FirewallRule rule = CreatingRule();
        Mock<IActivityRecorder> activity = new();

        await Handler(activity.Object).HandleAsync(Report(rule, "Observed", exists: true, state: "enabled"));

        Assert.Equal(FirewallRuleState.enabled, rule.CurrentState);
        Assert.Equal(FirewallRuleOperationKind.Create, rule.Pending!.Kind);
        activity.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("enabled", FirewallRuleState.enabled)]
    [InlineData("disabled", FirewallRuleState.disabled)]
    [InlineData("logged", FirewallRuleState.logged)]
    public async Task Observed_RecordsTheIncusStateAsIs(string word, FirewallRuleState expected)
    {
        FirewallRule rule = CreatingRule();
        rule.MarkActive("rule-9");

        await Handler().HandleAsync(Report(rule, "Observed", exists: true, state: word));

        Assert.Equal(expected, rule.CurrentState);
    }

    // Incus spells the state in lower case; anything else is not an Incus word.
    [Theory]
    [InlineData("Enabled")]
    [InlineData("Active")]
    [InlineData("0")]
    [InlineData(null)]
    public async Task Observed_AWordIncusDoesNotUse_IsRefused_AndChangesNothing(string? word)
    {
        FirewallRule rule = CreatingRule();
        rule.MarkActive("rule-9");

        BadRequestException ex = await Assert.ThrowsAsync<BadRequestException>(
            () => Handler().HandleAsync(Report(rule, "Observed", exists: true, state: word)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(FirewallRuleState.enabled, rule.CurrentState);
    }

    [Fact] // the reconciler does not find the rule in its ACL: never the delete done
    public async Task ObservedAbsent_OnAWaitingDelete_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        FirewallRule rule = DeletingRule();

        await Handler().HandleAsync(Report(rule, "Observed", exists: false));

        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
    }

    [Fact]
    public async Task ObservedWithoutExists_IsRefused()
    {
        FirewallRule rule = DeletingRule();

        BadRequestException ex = await Assert.ThrowsAsync<BadRequestException>(
            () => Handler().HandleAsync(Report(rule, "Observed")));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task Created_WithoutAProviderId_IsRefused()
    {
        FirewallRule rule = CreatingRule();

        BadRequestException ex = await Assert.ThrowsAsync<BadRequestException>(
            () => Handler().HandleAsync(Report(rule, "Created", externalRuleId: "")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Null(rule.ExternalId);
    }

    [Fact]
    public async Task RequestFailed_WhileDeleting_GivesTheRuleBack_WithTheReason_AndRecordsDeleteFailedOnce()
    {
        FirewallRule rule = DeletingRule();
        Mock<IActivityRecorder> activity = new();
        UpdateFirewallRuleStatusCommandHandler handler = Handler(activity.Object);

        await handler.HandleAsync(Report(rule, "RequestFailed", reason: "Firewall rule deletion failed.", detail: "raw"));
        await handler.HandleAsync(Report(rule, "RequestFailed", reason: "Firewall rule deletion failed.", detail: "raw"));

        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Null(rule.Transition);
        Assert.Equal("Firewall rule deletion failed.", rule.OperationFailureReason);
        Assert.Equal("raw", rule.OperationFailureDetail);
        Assert.Equal("rule-7", rule.ExternalId);
        activity.Verify(a => a.StageOutcome(rule.TenantId, "firewall_rule.delete_failed", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
    }

    [Fact] // the create report was lost, the give-up report carries the id
    public async Task RequestFailed_OfTheCreate_CarryingTheId_KeepsTheProviderId_AndTheReason_AndRecordsNothing()
    {
        FirewallRule rule = CreatingRule();
        Mock<IActivityRecorder> activity = new();

        await Handler(activity.Object).HandleAsync(Report(rule, "RequestFailed", externalRuleId: "rule-9", reason: "Firewall rule creation failed.", detail: "raw"));

        Assert.Equal("rule-9", rule.ExternalId);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
        Assert.Equal("Firewall rule creation failed.", rule.OperationFailureReason);
        Assert.Null(rule.Transition);
        activity.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Purged")]
    [InlineData("Active")]
    [InlineData("Absent")]
    [InlineData("Error")]
    public async Task AReportTheWorkerNoLongerSends_IsRefused_BeforeTheNetworkIsRead(string report)
    {
        FirewallRule rule = DeletingRule();

        BadRequestException ex = await Assert.ThrowsAsync<BadRequestException>(
            () => Handler().HandleAsync(Report(rule, report, externalRuleId: "rule-7")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // The network the route names is not there: the caller gets the rule's 404.
    [Fact]
    public async Task AnUnknownNetwork_IsFirewallRuleNotFound()
    {
        FirewallRule rule = CreatingRule();
        UpdateFirewallRuleStatusCommand report = new(Guid.NewGuid(), rule.Id, "Deleted");

        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(() => Handler().HandleAsync(report));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
    }

    // A rule is found only under the network the route names (titan-ops features/network/design.md#firewall-rule-soft-delete).
    [Fact]
    public async Task ARuleOfAnotherNetwork_IsFirewallRuleNotFound_AndIsLeftAlone()
    {
        Network other = ActiveNetwork();
        FirewallRule rule = other.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");

        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(
            () => Handler().HandleAsync(Report(rule, "Created", externalRuleId: "rule-9")));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
        Assert.Null(rule.ExternalId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
