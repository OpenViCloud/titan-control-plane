using Moq;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Domain.Aggregates.ClusterIpv6Route;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The request routes of titan-ops architecture/resource-lifecycle.md#callback-routes for a network and its firewall rules,
// at the handler: a report ends the request it names and records its outcome once; an observation writes current and
// ends nothing. The old route's mapping onto the same shape is at the end of this file and, in more detail, in
// NetworkServiceTests (UpdateStatus_*) and FirewallRuleStatusCallbackTests.
public class NetworkRequestHandlerTests
{
    private const string Block56 = "2001:41d0:d00:d400::/56";

    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    // Only the locked read is set up: a handler that read the network unlocked would find nothing and answer 404.
    private readonly Mock<INetworkRepository> _repo = new();
    private readonly Mock<IClusterIpv6RouteRepository> _routes = new();
    private readonly List<ClusterIpv6Route> _routeRows =
        Enumerable.Range(1, 255).Select(n => ClusterIpv6Route.Create("cluster-1", n)).ToList();
    private readonly FakeActivityRecorder _activity = new();

    public NetworkRequestHandlerTests() =>
        _routes.Setup(r => r.FindHeldByAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid networkId, CancellationToken _) => _routeRows.SingleOrDefault(route => route.NetworkId == networkId));

    private EndNetworkRequestCommandHandler EndHandler() => new(_repo.Object, new NetworkRoutes(_routes.Object), _activity);
    private ObserveNetworkCommandHandler ObserveHandler() => new(_repo.Object);
    private EndFirewallRuleRequestCommandHandler EndRuleHandler() => new(_repo.Object, _activity);
    private ObserveFirewallRuleCommandHandler ObserveRuleHandler() => new(_repo.Object);
    private UpdateNetworkStatusCommandHandler OldHandler() => new(_repo.Object, new NetworkRoutes(_routes.Object), _activity);
    private UpdateFirewallRuleStatusCommandHandler OldRuleHandler() => new(_repo.Object, _activity);

    private Network Given(Network network)
    {
        _repo.Setup(r => r.FindByIdForUpdateAsync(network.Id, It.IsAny<CancellationToken>())).ReturnsAsync(network);
        return network;
    }

    private static Network NewNetwork() => TestAggregates.Network(Owner, "net-1", "cluster-1");

    private static Network ActiveNetwork()
    {
        Network network = NewNetwork();
        network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        return network;
    }

    private ClusterIpv6Route RouteRow(int number) => _routeRows.Single(route => route.Number == number);

    // A network whose create waits and that holds route 1, as the create handler leaves them.
    private Network CreatingNetworkHoldingARoute()
    {
        Network network = Network.Create(Owner.Id, NetworkName.Parse("holder"), "cluster-1", null, wantsPublicIpv6: true);
        RouteRow(1).HandTo(network.Id);
        network.ReceivePublicIpv6(RouteRow(1).SubnetIn(Block56));
        return Given(network);
    }

    private static FirewallRule NewRule(Network network) =>
        network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");

    private static FirewallRule ActiveRule(Network network)
    {
        FirewallRule rule = NewRule(network);
        rule.MarkActive("rule-ext");
        return rule;
    }

    private IEnumerable<string> Outcomes() => _activity.Events.Where(e => e.OutcomeTenantId != null).Select(e => e.Action);

    private void NothingSaved() => _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);

    // ---------- the network's request report ----------

    [Fact]
    public async Task Done_OfTheCreate_StoresTheIdAddressAndStatus_RecordsNetworkCreatedOnce_AndSaves()
    {
        Network network = Given(NewNetwork());

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, network.Pending!.Id, "Done", "net-ext",
            Status: "Created", Cidr: "10.0.0.0/24", Gateway: "10.0.0.1"));

        Assert.Null(network.Pending);
        Assert.Equal(("net-ext", CurrentExistence.Exists, NetworkStatus.Created),
            (network.ExternalId, network.CurrentExistence, network.CurrentState));
        Assert.Equal(("10.0.0.0/24", "10.0.0.1"), (network.Cidr, network.Gateway));
        var created = Assert.Single(_activity.Events);
        Assert.Equal(("network.created", Owner.Id, network.Id), (created.Action, created.OutcomeTenantId, created.ResourceId));
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // The worker repeats a report it could not confirm was received: the second finds nothing waiting.
    [Fact]
    public async Task Done_OfTheCreate_Twice_TheSecondSavesNothing_AndRecordsNothing()
    {
        Network network = Given(NewNetwork());
        Guid create = network.Pending!.Id;
        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, create, "Done", "net-ext"));

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, create, "Done", "net-ext"));

        Assert.Equal(["network.created"], Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Done_OfTheCreate_WithoutTheNetworkName_Is422_AndSavesNothing(string? externalId)
    {
        Network network = Given(NewNetwork());

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, network.Pending!.Id, "Done", externalId)));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.Equal(NetworkOperationKind.Create, network.Pending!.Kind);
        Assert.Empty(_activity.Events);
        NothingSaved();
    }

    // A failed create records nothing: a failure is not the create's outcome
    // (titan-ops architecture/resource-lifecycle.md#kinds). A network Incus made is kept, so its delete still reaches Incus.
    [Fact]
    public async Task Failed_OfTheCreate_RecordsTheReason_KeepsANetworkIncusMade_AndRecordsNoOutcome()
    {
        Network network = Given(NewNetwork());

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, network.Pending!.Id, "Failed", "net-ext",
            Reason: "Network creation failed.", Detail: "raw"));

        Assert.Null(network.Pending);
        Assert.Equal(("net-ext", CurrentExistence.Exists), (network.ExternalId, network.CurrentExistence));
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal(("Network creation failed.", "raw"), (network.OperationFailureReason, network.OperationFailureDetail));
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // Incus never had the network, so nothing holds the /64 there: the route goes back in the same save
    // (titan-ops features/network/design.md#ipv6-public).
    [Fact]
    public async Task Failed_OfTheCreate_WithoutAnId_OnAnIpv6Network_GivesTheRouteBack()
    {
        Network network = CreatingNetworkHoldingARoute();

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, network.Pending!.Id, "Failed",
            Reason: "Network creation failed."));

        Assert.False(network.HasPublicIpv6);
        Assert.True(RouteRow(1).IsFree);
        Assert.NotNull(RouteRow(1).ReleasedAt);
    }

    [Fact]
    public async Task Failed_OfTheCreate_WithAnId_OnAnIpv6Network_KeepsTheRoute()
    {
        Network network = CreatingNetworkHoldingARoute();

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, network.Pending!.Id, "Failed", "net-ext",
            Reason: "Network creation failed."));

        Assert.True(network.HasPublicIpv6);
        Assert.Equal(network.Id, RouteRow(1).NetworkId);
    }

    // The delete took the network's ACL and every rule in it (titan-ops features/network/design.md#network-lifecycle):
    // each rule left is Deleted, and only a rule whose own delete waited gets its outcome.
    [Fact]
    public async Task Done_OfTheDelete_RecordsNetworkDeleted_AndFirewallRuleDeletedForTheRulesWhoseDeleteWaited()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule staying = ActiveRule(network);
        FirewallRule leaving = ActiveRule(network);
        network.DeleteFirewallRule(leaving.Id);
        OperationTicket delete = network.Delete()!;

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, delete.OperationId, "Done"));

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Null(network.Pending);
        Assert.Equal((CurrentExistence.Deleted, CurrentExistence.Deleted), (staying.CurrentExistence, leaving.CurrentExistence));
        Assert.Equal(["firewall_rule.deleted", "network.deleted"], Outcomes().OrderBy(a => a, StringComparer.Ordinal));
        Assert.Equal(leaving.Id, _activity.Events.Single(e => e.Action == "firewall_rule.deleted").ResourceId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheDelete_TakesTheRouteBack()
    {
        Network network = CreatingNetworkHoldingARoute();
        network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        OperationTicket delete = network.Delete()!;

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, delete.OperationId, "Done"));

        Assert.True(RouteRow(1).IsFree);
        Assert.NotNull(RouteRow(1).ReleasedAt);
    }

    [Fact]
    public async Task Failed_OfTheDelete_PutsDesiredBack_RecordsTheReason_AndNetworkDeleteFailedOnce_KeepingTheRoute()
    {
        Network network = CreatingNetworkHoldingARoute();
        network.MarkActive("net-ext", "10.0.0.0/24", "10.0.0.1");
        OperationTicket delete = network.Delete()!;

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, delete.OperationId, "Failed",
            Reason: "Network deletion failed.", Detail: "raw"));

        Assert.Null(network.Pending);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal("Network deletion failed.", network.OperationFailureReason);
        Assert.Equal(["network.delete_failed"], Outcomes());
        Assert.Equal(network.Id, RouteRow(1).NetworkId);
    }

    [Fact]
    public async Task AReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        Network network = Given(ActiveNetwork());
        OperationTicket delete = network.Delete()!;

        await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, Guid.NewGuid(), "Failed", Reason: "stale"));

        Assert.Equal(delete.OperationId, network.Pending!.Id);
        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Null(network.OperationFailureReason);
        Assert.Empty(_activity.Events);
        NothingSaved();
    }

    [Theory]
    [InlineData("Maybe", null)]
    [InlineData("done", null)]
    [InlineData("Done", "Active")]
    [InlineData("Done", "created")]
    public async Task AReport_WithAnUnknownOutcomeOrStatus_Is400_BeforeTheNetworkIsRead(string outcome, string? status)
    {
        var refused = await Assert.ThrowsAsync<BadRequestException>(() =>
            EndHandler().HandleAsync(new EndNetworkRequestCommand(Guid.NewGuid(), Guid.NewGuid(), outcome, "net-ext", Status: status)));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_ForANetworkThatIsNotThere_Is404()
    {
        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            EndHandler().HandleAsync(new EndNetworkRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Done", "net-ext")));

        Assert.Equal("NETWORK_NOT_FOUND", refused.Code);
    }

    // ---------- the reconciler's observation of the network ----------

    // An observation ends no request (titan-ops architecture/resource-lifecycle.md#invariants): Created or Errored while
    // the create waits is the network's state, not the create's ending.
    [Theory]
    [InlineData("Created", NetworkStatus.Created)]
    [InlineData("Errored", NetworkStatus.Errored)]
    public async Task AnObservation_WhileTheCreateWaits_WritesCurrent_AndTheCreateKeepsWaiting(string word, NetworkStatus expected)
    {
        Network network = Given(NewNetwork());
        Guid create = network.Pending!.Id;

        await ObserveHandler().HandleAsync(new ObserveNetworkCommand(network.Id, true, word));

        Assert.Equal((CurrentExistence.Exists, expected), (network.CurrentExistence, network.CurrentState));
        Assert.Equal(create, network.Pending!.Id);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    // The reconciler never reports the delete done (titan-ops architecture/desired-and-current.md#existence-axis).
    [Fact]
    public async Task AnObservation_NotFound_WritesNotFound_AndLeavesTheDeleteWaiting()
    {
        Network network = Given(ActiveNetwork());
        OperationTicket delete = network.Delete()!;

        await ObserveHandler().HandleAsync(new ObserveNetworkCommand(network.Id, false));

        Assert.Equal(CurrentExistence.NotFound, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal(delete.OperationId, network.Pending!.Id);
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("created")]
    [InlineData(null)]
    public async Task AnObservation_WithAnUnknownStatus_Is400_BeforeTheNetworkIsRead(string? word)
    {
        var refused = await Assert.ThrowsAsync<BadRequestException>(() =>
            ObserveHandler().HandleAsync(new ObserveNetworkCommand(Guid.NewGuid(), true, word)));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AnObservation_ForANetworkThatIsNotThere_Is404()
    {
        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            ObserveHandler().HandleAsync(new ObserveNetworkCommand(Guid.NewGuid(), false)));

        Assert.Equal("NETWORK_NOT_FOUND", refused.Code);
    }

    // ---------- a firewall rule's request report and observation ----------

    [Fact]
    public async Task RuleDone_OfTheCreate_StoresTheIdAndState_AndRecordsFirewallRuleCreatedOnce()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = NewRule(network);
        Guid create = rule.Pending!.Id;

        await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, create, "Done", "rule-ext", "enabled"));
        await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, create, "Done", "rule-ext", "enabled"));

        Assert.Null(rule.Pending);
        Assert.Equal(("rule-ext", CurrentExistence.Exists, FirewallRuleState.enabled),
            (rule.ExternalId, rule.CurrentExistence, rule.CurrentState));
        var created = Assert.Single(_activity.Events);
        Assert.Equal(("firewall_rule.created", Owner.Id, rule.Id), (created.Action, created.OutcomeTenantId, created.ResourceId));
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RuleDone_OfTheCreate_WithoutTheRuleId_Is422_AndSavesNothing()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = NewRule(network);

        var refused = await Assert.ThrowsAsync<BusinessException>(() => EndRuleHandler().HandleAsync(
            new EndFirewallRuleRequestCommand(network.Id, rule.Id, rule.Pending!.Id, "Done")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.Equal(FirewallRuleOperationKind.Create, rule.Pending!.Kind);
        NothingSaved();
    }

    [Fact]
    public async Task RuleFailed_OfTheCreate_RecordsTheReason_AndRecordsNoOutcome()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = NewRule(network);

        await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, rule.Pending!.Id, "Failed",
            Reason: "Firewall rule creation failed.", Detail: "raw"));

        Assert.Null(rule.Pending);
        Assert.Null(rule.ExternalId);
        Assert.Equal(("Firewall rule creation failed.", "raw"), (rule.OperationFailureReason, rule.OperationFailureDetail));
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task RuleDone_OfTheDelete_MarksTheRuleDeleted_AndRecordsFirewallRuleDeletedOnce()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = ActiveRule(network);
        OperationTicket delete = network.DeleteFirewallRule(rule.Id)!;

        await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, delete.OperationId, "Done"));
        await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, delete.OperationId, "Done"));

        Assert.Equal(CurrentExistence.Deleted, rule.CurrentExistence);
        Assert.Equal(["firewall_rule.deleted"], Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RuleFailed_OfTheDelete_GivesTheRuleBack_AndRecordsFirewallRuleDeleteFailedOnce()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = ActiveRule(network);
        OperationTicket delete = network.DeleteFirewallRule(rule.Id)!;

        await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, delete.OperationId, "Failed",
            Reason: "Firewall rule deletion failed."));

        Assert.Null(rule.Pending);
        Assert.Equal(DesiredExistence.Exists, rule.DesiredExistence);
        Assert.Equal("Firewall rule deletion failed.", rule.OperationFailureReason);
        Assert.Equal(["firewall_rule.delete_failed"], Outcomes());
    }

    [Fact]
    public async Task ARuleReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = ActiveRule(network);
        OperationTicket delete = network.DeleteFirewallRule(rule.Id)!;

        await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, Guid.NewGuid(), "Done"));

        Assert.Equal(delete.OperationId, rule.Pending!.Id);
        Assert.Empty(_activity.Events);
        NothingSaved();
    }

    [Theory]
    [InlineData("Maybe", null)]
    [InlineData("Done", "Enabled")]
    [InlineData("Done", "0")]
    public async Task ARuleReport_WithAnUnknownOutcomeOrState_Is400_BeforeTheNetworkIsRead(string outcome, string? state)
    {
        var refused = await Assert.ThrowsAsync<BadRequestException>(() => EndRuleHandler().HandleAsync(
            new EndFirewallRuleRequestCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), outcome, "rule-ext", state)));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ARuleReport_ForANetworkThatIsNotThere_IsFirewallRuleNotFound()
    {
        var refused = await Assert.ThrowsAsync<NotFoundException>(() => EndRuleHandler().HandleAsync(
            new EndFirewallRuleRequestCommand(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Done", "rule-ext")));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", refused.Code);
    }

    // A rule is found only under the network the route names (titan-ops features/network/design.md#firewall-rule-soft-delete).
    [Fact]
    public async Task ARuleReport_UnderAnotherNetwork_IsFirewallRuleNotFound_AndLeavesTheRuleWaiting()
    {
        Network network = Given(ActiveNetwork());
        Network other = Given(ActiveNetwork());
        FirewallRule rule = NewRule(other);

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => EndRuleHandler().HandleAsync(
            new EndFirewallRuleRequestCommand(network.Id, rule.Id, rule.Pending!.Id, "Done", "rule-ext")));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", refused.Code);
        Assert.Equal(FirewallRuleOperationKind.Create, rule.Pending!.Kind);
        NothingSaved();
    }

    [Fact]
    public async Task ARuleObservation_WhileTheCreateWaits_WritesCurrent_AndTheCreateKeepsWaiting()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = NewRule(network);
        Guid create = rule.Pending!.Id;

        await ObserveRuleHandler().HandleAsync(new ObserveFirewallRuleCommand(network.Id, rule.Id, true, "enabled"));

        Assert.Equal((CurrentExistence.Exists, FirewallRuleState.enabled), (rule.CurrentExistence, rule.CurrentState));
        Assert.Equal(create, rule.Pending!.Id);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ARuleObservation_NotFound_WritesNotFound_AndLeavesTheDeleteWaiting()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = ActiveRule(network);
        OperationTicket delete = network.DeleteFirewallRule(rule.Id)!;

        await ObserveRuleHandler().HandleAsync(new ObserveFirewallRuleCommand(network.Id, rule.Id, false));

        Assert.Equal(CurrentExistence.NotFound, rule.CurrentExistence);
        Assert.Equal(delete.OperationId, rule.Pending!.Id);
    }

    [Fact]
    public async Task ARuleObservation_UnderAnotherNetwork_IsFirewallRuleNotFound()
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = ActiveRule(Given(ActiveNetwork()));

        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            ObserveRuleHandler().HandleAsync(new ObserveFirewallRuleCommand(network.Id, rule.Id, false)));

        Assert.Equal("FIREWALL_RULE_NOT_FOUND", refused.Code);
        Assert.Equal(CurrentExistence.Exists, rule.CurrentExistence);
    }

    [Fact]
    public async Task ARuleObservation_WithAnUnknownState_Is400_BeforeTheNetworkIsRead()
    {
        var refused = await Assert.ThrowsAsync<BadRequestException>(() =>
            ObserveRuleHandler().HandleAsync(new ObserveFirewallRuleCommand(Guid.NewGuid(), Guid.NewGuid(), true, "Enabled")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---------- the old route, mapped onto the waiting request ----------

    [Fact]
    public async Task OldRoute_Created_IsTheCreatesDone_RecordsNetworkCreatedOnce_TheReconcilersLaterCreatedRecordsNothing()
    {
        Network network = Given(NewNetwork());

        await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "Created", ExternalId: "net-ext",
            Cidr: "10.0.0.0/24", Gateway: "10.0.0.1"));
        await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "Observed", Exists: true, Status: "Created"));

        Assert.Null(network.Pending);
        Assert.Equal(("net-ext", NetworkStatus.Created), (network.ExternalId, network.CurrentState));
        Assert.Equal(["network.created"], Outcomes());
    }

    [Fact]
    public async Task OldRoute_RequestFailed_OnAWaitingDelete_IsTheDeletesFailed_AndRecordsNetworkDeleteFailed()
    {
        Network network = Given(ActiveNetwork());
        network.Delete();

        await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "RequestFailed",
            Reason: "Network deletion failed.", Detail: "raw"));

        Assert.Null(network.Pending);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal("Network deletion failed.", network.OperationFailureReason);
        Assert.Equal(["network.delete_failed"], Outcomes());
    }

    [Fact]
    public async Task OldRoute_RequestFailed_OnACreateCarryingTheId_KeepsTheId_AndRecordsNothing()
    {
        Network network = Given(NewNetwork());

        await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "RequestFailed", ExternalId: "net-ext",
            Reason: "Network creation failed."));

        Assert.Null(network.Pending);
        Assert.Equal(("net-ext", CurrentExistence.Exists), (network.ExternalId, network.CurrentExistence));
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task OldRoute_RequestFailed_OnAnIpv6CreateWithoutAnId_GivesTheRouteBack()
    {
        Network network = CreatingNetworkHoldingARoute();

        await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "RequestFailed", Reason: "Network creation failed."));

        Assert.False(network.HasPublicIpv6);
        Assert.True(RouteRow(1).IsFree);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task OldRoute_Deleted_WithADeleteWaiting_IsTheDeletesDone_AndRecordsNetworkDeleted()
    {
        Network network = Given(ActiveNetwork());
        network.Delete();

        await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "Deleted"));

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Null(network.Pending);
        Assert.Equal(["network.deleted"], Outcomes());
    }

    [Fact]
    public async Task OldRoute_Deleted_WithNothingWaiting_IsNotFound_AndRecordsNothing()
    {
        Network network = Given(ActiveNetwork());

        await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "Deleted"));

        Assert.Equal(CurrentExistence.NotFound, network.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Empty(_activity.Events);
    }

    // ---------- every lifecycle handler takes the network's row lock ----------

    // Locked, so a report and a request on the same network take turns
    // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Theory]
    [InlineData("EndNetworkRequest")]
    [InlineData("ObserveNetwork")]
    [InlineData("EndFirewallRuleOperation")]
    [InlineData("ObserveFirewallRule")]
    [InlineData("UpdateNetworkStatus")]
    [InlineData("UpdateFirewallRuleStatus")]
    public async Task EveryHandler_LoadsTheNetworkLocked(string handler)
    {
        Network network = Given(ActiveNetwork());
        FirewallRule rule = ActiveRule(network);

        switch (handler)
        {
            case "EndNetworkRequest":
                await EndHandler().HandleAsync(new EndNetworkRequestCommand(network.Id, Guid.NewGuid(), "Done"));
                break;
            case "ObserveNetwork":
                await ObserveHandler().HandleAsync(new ObserveNetworkCommand(network.Id, true, "Created"));
                break;
            case "EndFirewallRuleOperation":
                await EndRuleHandler().HandleAsync(new EndFirewallRuleRequestCommand(network.Id, rule.Id, Guid.NewGuid(), "Done"));
                break;
            case "ObserveFirewallRule":
                await ObserveRuleHandler().HandleAsync(new ObserveFirewallRuleCommand(network.Id, rule.Id, true, "enabled"));
                break;
            case "UpdateNetworkStatus":
                await OldHandler().HandleAsync(new UpdateNetworkStatusCommand(network.Id, "Observed", Exists: true, Status: "Created"));
                break;
            default:
                await OldRuleHandler().HandleAsync(new UpdateFirewallRuleStatusCommand(network.Id, rule.Id, "Observed", Exists: true, State: "enabled"));
                break;
        }

        _repo.Verify(r => r.FindByIdForUpdateAsync(network.Id, It.IsAny<CancellationToken>()), Times.Once);
        _repo.Verify(r => r.FindByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _repo.Verify(r => r.FindByFirewallRuleIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
