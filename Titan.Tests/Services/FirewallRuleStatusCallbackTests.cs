using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Domain.Aggregates.FirewallRule;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;

namespace Titan.Tests.Services;

// The firewall rule callback (titan-ops features/network/design.md#firewall-rule-soft-delete): four reports, the Incus
// ACL rule state word for word (titan-ops architecture/desired-and-current.md#provider-state-is-verbatim).
public class FirewallRuleStatusCallbackTests
{
    private static readonly Guid NetworkId = Guid.NewGuid();

    private static UpdateFirewallRuleStatusCommandHandler HandlerFor(FirewallRule rule, IActivityRecorder activity)
    {
        Mock<IFirewallRuleRepository> repo = new();
        repo.Setup(r => r.FindByIdAsync(rule.Id, It.IsAny<CancellationToken>())).ReturnsAsync(rule);
        return new UpdateFirewallRuleStatusCommandHandler(repo.Object, activity);
    }

    private static UpdateFirewallRuleStatusCommandHandler HandlerFor(FirewallRule rule) =>
        HandlerFor(rule, Mock.Of<IActivityRecorder>());

    private static UpdateFirewallRuleStatusCommand Report(FirewallRule rule, string report, bool? exists = null,
        string? state = null, string? externalRuleId = null, string? reason = null, string? detail = null) =>
        new(NetworkId, rule.Id, report, exists, state, externalRuleId, reason, detail);

    private static FirewallRule CreatingRule() =>
        FirewallRule.CreateUnchecked(NetworkId, Guid.NewGuid(),
            FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");

    private static FirewallRule DeletingRule()
    {
        FirewallRule rule = CreatingRule();
        rule.MarkActive("rule-7");
        rule.RequestDelete();
        return rule;
    }

    [Fact]
    public async Task Deleted_MarksTheRuleDeleted_AndRecordsTheOutcomeOnce()
    {
        FirewallRule rule = DeletingRule();
        Mock<IActivityRecorder> activity = new();
        UpdateFirewallRuleStatusCommandHandler handler = HandlerFor(rule, activity.Object);

        await handler.HandleAsync(Report(rule, "Deleted"));
        await handler.HandleAsync(Report(rule, "Deleted"));

        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Equal("rule-7", rule.ExternalId);
        activity.Verify(a => a.StageOutcome(rule.TenantId, "firewall_rule.deleted", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Created_StoresTheProviderId_AndTheCreateKeepsWaiting()
    {
        FirewallRule rule = CreatingRule();

        await HandlerFor(rule).HandleAsync(Report(rule, "Created", externalRuleId: "rule-9"));

        Assert.Equal("rule-9", rule.ExternalId);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
        Assert.Null(rule.CurrentState);
        Assert.Equal(FirewallRuleTransition.Creating, rule.Transition);
    }

    [Fact] // the reconciler sees the rule after the create command reported its id
    public async Task ObservedAfterCreated_RecordsTheCreatedOutcomeOnce()
    {
        FirewallRule rule = CreatingRule();
        Mock<IActivityRecorder> activity = new();
        UpdateFirewallRuleStatusCommandHandler handler = HandlerFor(rule, activity.Object);

        await handler.HandleAsync(Report(rule, "Created", externalRuleId: "rule-9"));
        await handler.HandleAsync(Report(rule, "Observed", exists: true, state: "enabled"));
        await handler.HandleAsync(Report(rule, "Observed", exists: true, state: "enabled"));

        Assert.Equal(FirewallRuleState.enabled, rule.CurrentState);
        Assert.Null(rule.Transition);
        activity.Verify(a => a.StageOutcome(rule.TenantId, "firewall_rule.created", "firewall_rule", rule.Id, It.IsAny<string>()), Times.Once);
    }

    [Theory]
    [InlineData("enabled", FirewallRuleState.enabled)]
    [InlineData("disabled", FirewallRuleState.disabled)]
    [InlineData("logged", FirewallRuleState.logged)]
    public async Task Observed_RecordsTheIncusStateAsIs(string word, FirewallRuleState expected)
    {
        FirewallRule rule = CreatingRule();
        rule.MarkActive("rule-9");

        await HandlerFor(rule).HandleAsync(Report(rule, "Observed", exists: true, state: word));

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
            () => HandlerFor(rule).HandleAsync(Report(rule, "Observed", exists: true, state: word)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(FirewallRuleState.enabled, rule.CurrentState);
    }

    [Fact] // the reconciler does not find the rule in its ACL: never the delete done
    public async Task ObservedAbsent_OnAWaitingDelete_IsNotFound_AndTheDeleteKeepsWaiting()
    {
        FirewallRule rule = DeletingRule();

        await HandlerFor(rule).HandleAsync(Report(rule, "Observed", exists: false));

        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
    }

    [Fact]
    public async Task ObservedWithoutExists_IsRefused()
    {
        FirewallRule rule = DeletingRule();

        BadRequestException ex = await Assert.ThrowsAsync<BadRequestException>(
            () => HandlerFor(rule).HandleAsync(Report(rule, "Observed")));

        Assert.Equal("INVALID_STATUS", ex.Code);
    }

    [Fact]
    public async Task Created_WithoutAProviderId_IsRefused()
    {
        FirewallRule rule = CreatingRule();

        BadRequestException ex = await Assert.ThrowsAsync<BadRequestException>(
            () => HandlerFor(rule).HandleAsync(Report(rule, "Created", externalRuleId: "")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Null(rule.ExternalId);
    }

    [Fact]
    public async Task RequestFailed_WhileDeleting_GivesTheRuleBack_WithTheReason()
    {
        FirewallRule rule = DeletingRule();

        await HandlerFor(rule).HandleAsync(Report(rule, "RequestFailed", reason: "Firewall rule deletion failed.", detail: "raw"));

        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Null(rule.Transition);
        Assert.Equal("Firewall rule deletion failed.", rule.RequestFailureReason);
        Assert.Equal("raw", rule.RequestFailureDetail);
        Assert.Equal("rule-7", rule.ExternalId);
    }

    [Fact] // the create report was lost, the give-up report carries the id
    public async Task RequestFailed_OfTheCreate_CarryingTheId_KeepsTheProviderId_AndTheReason()
    {
        FirewallRule rule = CreatingRule();

        await HandlerFor(rule).HandleAsync(Report(rule, "RequestFailed", externalRuleId: "rule-9", reason: "Firewall rule creation failed.", detail: "raw"));

        Assert.Equal("rule-9", rule.ExternalId);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
        Assert.Equal("Firewall rule creation failed.", rule.RequestFailureReason);
        Assert.Null(rule.Transition);
    }

    [Theory]
    [InlineData("Purged")]
    [InlineData("Active")]
    [InlineData("Absent")]
    [InlineData("Error")]
    public async Task AReportTheWorkerNoLongerSends_IsRefused_AndChangesNothing(string report)
    {
        FirewallRule rule = DeletingRule();

        BadRequestException ex = await Assert.ThrowsAsync<BadRequestException>(
            () => HandlerFor(rule).HandleAsync(Report(rule, report, externalRuleId: "rule-7")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.Equal(FirewallRuleTransition.Deleting, rule.Transition);
    }

    [Fact]
    public async Task AnUnknownRule_IsNotFound()
    {
        FirewallRule rule = CreatingRule();
        Mock<IFirewallRuleRepository> repo = new();
        UpdateFirewallRuleStatusCommandHandler handler = new(repo.Object, Mock.Of<IActivityRecorder>());

        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(Report(rule, "Deleted")));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", ex.Code);
    }
}
