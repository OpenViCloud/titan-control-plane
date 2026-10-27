using Titan.Domain.Aggregates.Networking.Network;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

public class NetworkTests
{
    // ── Provider id is handed out, never asserted by the caller (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#aggregate-hands-out-provider-id) ──

    [Fact]
    public void RequestDelete_YieldsTheProviderId()
    {
        var network = MakeActiveNetwork();
        Assert.Equal("cs-net-id", network.RequestDelete());
    }

    [Fact]
    public void BeginVmDeployment_WhenCreated_YieldsTheProviderId_AndMutatesNothing()
    {
        var network = MakeActiveNetwork();
        Assert.Equal("cs-net-id", network.BeginVmDeployment("zone-1"));
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
    }

    // DeleteRequested: Incus still shows the network Created, and a VM must still not land on it.
    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.Errored)]
    [InlineData(Seed.Pending)]
    [InlineData(Seed.NotFound)]
    public void BeginVmDeployment_WhenNotUsable_Throws(Seed seed)
    {
        var network = NetworkIn(seed);
        Assert.Equal("NETWORK_NOT_ACTIVE", Assert.Throws<BusinessException>(() => network.BeginVmDeployment("zone-1")).Code);
    }

    // A VM lands on its network's cluster (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#zone-is-a-real-choice).
    [Fact]
    public void BeginVmDeployment_ForAVmOfAnotherZone_IsRefused()
    {
        var network = MakeActiveNetwork();
        var ex = Assert.Throws<BusinessException>(() => network.BeginVmDeployment("other-zone"));
        Assert.Equal("NETWORK_CLUSTER_MISMATCH", ex.Code);
    }

    [Fact]
    public void BeginVmDeployment_AnotherZone_IsAnsweredBeforeTheState()
    {
        var network = NetworkIn(Seed.Creating);
        var ex = Assert.Throws<BusinessException>(() => network.BeginVmDeployment("other-zone"));
        Assert.Equal("NETWORK_CLUSTER_MISMATCH", ex.Code);
    }

    [Fact]
    public void BeginFirewallRule_WhenCreated_YieldsTheProviderId_AndMutatesNothing()
    {
        var network = MakeActiveNetwork();
        Assert.Equal("cs-net-id", network.BeginFirewallRule());
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    // A DIFFERENT code from BeginVmDeployment, on purpose: the firewall endpoint answers FIREWALL_NETWORK_NOT_READY.
    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    [InlineData(Seed.CreateFailed)]
    [InlineData(Seed.Errored)]
    [InlineData(Seed.NotFound)]
    public void BeginFirewallRule_WhenNotUsable_Throws(Seed seed)
    {
        var network = NetworkIn(seed);
        Assert.Equal("FIREWALL_NETWORK_NOT_READY", Assert.Throws<BusinessException>(network.BeginFirewallRule).Code);
    }

    public enum Seed { Creating, Active, DeleteRequested, Deleted, CreateFailed, Errored, Pending, NotFound }

    private static Network NetworkIn(Seed seed)
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");
        switch (seed)
        {
            case Seed.Creating: break;
            case Seed.Active: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); break;
            case Seed.DeleteRequested: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.RequestDelete(); break;
            case Seed.Deleted: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.RequestDelete(); network.MarkDeleted(); break;
            case Seed.CreateFailed: network.MarkRequestFailed("reason", "detail"); break;
            case Seed.Errored: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.MarkObserved(NetworkStatus.Errored); break;
            case Seed.Pending: network.MarkCreated("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.MarkObserved(NetworkStatus.Pending); break;
            case Seed.NotFound: network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1"); network.MarkNotFound(); break;
            default: throw new ArgumentOutOfRangeException(nameof(seed), seed, "unmapped seed");
        }
        return network;
    }

    // ── Create: nothing on Incus yet, a create waiting ────

    [Fact]
    public void Create_HasNoIncusStatus_IsNotCreated_AndWaitsForItsCreate()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");

        Assert.Null(network.CurrentState);
        Assert.Equal(CurrentExistence.NotCreated, network.CurrentExistence);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
    }

    [Fact]
    public void MarkCreated_RecordsTheIdAndAddress_AndThatIncusHasIt_ButLeavesTheStatusToTheReconciler()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");

        network.MarkCreated("cs-net-id", "10.0.0.0/24", "10.0.0.1");

        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Equal("10.0.0.0/24", network.Cidr);
        Assert.Equal("10.0.0.1", network.Gateway);
        Assert.Equal(NetworkTransition.Creating, network.Transition);
    }

    [Fact]
    public void Created_FulfilsTheCreateOnce()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");
        network.MarkCreated("cs-net-id", "10.0.0.0/24", "10.0.0.1");

        bool first = network.MarkObserved(NetworkStatus.Created);
        bool repeat = network.MarkObserved(NetworkStatus.Created);

        Assert.True(first);
        Assert.False(repeat);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Null(network.Transition);
    }

    // Every word Incus reports is recorded as it is (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#provider-state-is-verbatim).
    [Theory]
    [InlineData(NetworkStatus.Pending)]
    [InlineData(NetworkStatus.Created)]
    [InlineData(NetworkStatus.Errored)]
    [InlineData(NetworkStatus.Unknown)]
    [InlineData(NetworkStatus.Unavailable)]
    public void MarkObserved_RecordsTheIncusWord_AndThatIncusHasIt(NetworkStatus status)
    {
        var network = MakeActiveNetwork();

        network.MarkObserved(status);

        Assert.Equal(status, network.CurrentState);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal("10.0.0.0/24", network.Cidr);
    }

    // Errored and Unavailable say the create will not finish; Pending and Unknown may still become Created.
    [Theory]
    [InlineData(NetworkStatus.Errored, true)]
    [InlineData(NetworkStatus.Unavailable, true)]
    [InlineData(NetworkStatus.Pending, false)]
    [InlineData(NetworkStatus.Unknown, false)]
    public void AFailedIncusStatus_EndsAWaitingCreate_OthersKeepItWaiting(NetworkStatus status, bool ends)
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");
        network.MarkCreated("cs-net-id", "10.0.0.0/24", "10.0.0.1");

        bool fulfilled = network.MarkObserved(status);

        Assert.False(fulfilled);
        if (ends)
        {
            Assert.Null(network.Transition);
        }
        else
        {
            Assert.Equal(NetworkTransition.Creating, network.Transition);
        }
    }

    [Theory]
    [InlineData(NetworkStatus.Errored)]
    [InlineData(NetworkStatus.Unavailable)]
    [InlineData(NetworkStatus.Created)]
    public void NoObservation_WithdrawsAWaitingDelete(NetworkStatus status)
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();

        bool fulfilled = network.MarkObserved(status);

        Assert.False(fulfilled);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
    }

    // ── Not found: the reconciler never reports Deleted (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#existence-axis) ──

    [Fact]
    public void NotFound_ClearsTheStatus_KeepsTheIdAndAddress()
    {
        var network = MakeActiveNetwork();

        network.MarkNotFound();

        Assert.Equal(CurrentExistence.NotFound, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
    }

    [Fact]
    public void NotFound_KeepsAWaitingDelete_OnlyTheDeleteCommandEndsIt()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();

        network.MarkNotFound();

        Assert.Equal(NetworkTransition.Deleting, network.Transition);
        Assert.False(network.IsTerminal);
        Assert.True(network.MarkDeleted());
    }

    [Fact]
    public void NotFound_EndsAWaitingCreate()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");
        network.MarkCreated("cs-net-id", "10.0.0.0/24", "10.0.0.1");

        network.MarkNotFound();

        Assert.Null(network.Transition);
    }

    [Fact]
    public void SeenAgainAfterNotFound_Exists()
    {
        var network = MakeActiveNetwork();
        network.MarkNotFound();

        network.MarkObserved(NetworkStatus.Created);

        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    // ── Request failure is a field, not a state (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#request-failure) ──

    [Fact]
    public void RequestFailed_OnAWaitingCreate_RecordsTheFailure_EndsTheCreate_AndLeavesItNotCreated()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");

        network.MarkRequestFailed("Network creation failed.", "raw incus error");

        Assert.Equal("Network creation failed.", network.RequestFailureReason);
        Assert.Equal("raw incus error", network.RequestFailureDetail);
        Assert.NotNull(network.RequestFailedAt);
        Assert.Null(network.Transition);
        Assert.Equal(CurrentExistence.NotCreated, network.CurrentExistence);
        Assert.Null(network.CurrentState);
    }

    [Fact]
    public void RequestFailed_WithNoCreateWaiting_IsStale_AndRecordsNothing()
    {
        var network = MakeActiveNetwork();

        network.MarkRequestFailed("late", "late");

        Assert.Null(network.RequestFailureReason);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
    }

    // A delete Incus refused gives the network back (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/desired-and-current.md#abandoned-request),
    // with the reason, and a second press is a new request.
    [Fact]
    public void AFailedDelete_GivesTheNetworkBack_WithTheReason_AndAnotherPressIsANewRequest()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();

        network.AbandonRequest("Network deletion failed.", "raw");

        Assert.Equal(DesiredExistence.Exists, network.DesiredExistence);
        Assert.Null(network.Transition);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal("Network deletion failed.", network.RequestFailureReason);

        network.RequestDelete();
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void AbandonRequest_WithNoDeleteWaiting_IsRefused()
    {
        var active = MakeActiveNetwork();
        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => active.AbandonRequest("r", "d")).Code);

        var deleted = NetworkIn(Seed.Deleted);
        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => deleted.AbandonRequest("r", "d")).Code);
    }

    // ── RequestDelete: follows Incus — whatever its word, a network Incus may hold is deleted through Incus ──

    [Fact]
    public void RequestDelete_WritesDesiredExistence_AndLeavesTheObservationAlone()
    {
        var network = MakeActiveNetwork();

        network.RequestDelete();

        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Equal(NetworkStatus.Created, network.CurrentState);
        Assert.Equal(CurrentExistence.Exists, network.CurrentExistence);
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Theory]
    [InlineData(NetworkStatus.Pending)]
    [InlineData(NetworkStatus.Errored)]
    [InlineData(NetworkStatus.Unknown)]
    [InlineData(NetworkStatus.Unavailable)]
    public void RequestDelete_WithAProviderId_IsAccepted_WhateverIncusSays(NetworkStatus status)
    {
        var network = MakeActiveNetwork();
        network.MarkObserved(status);

        Assert.Equal("cs-net-id", network.RequestDelete());
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void RequestDelete_OfANetworkIncusNoLongerHas_GoesThroughIncus()
    {
        var network = NetworkIn(Seed.NotFound);

        Assert.Equal("cs-net-id", network.RequestDelete());
        Assert.Equal(NetworkTransition.Deleting, network.Transition);
    }

    [Fact]
    public void RequestDelete_WhileTheCreateWaits_Throws()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");

        var ex = Assert.Throws<BusinessException>(() => network.RequestDelete());

        Assert.Equal("NETWORK_INVALID_STATE", ex.Code);
    }

    [Fact]
    public void RequestDelete_Twice_IsRefused_AndKeepsTheFirstRequest()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();
        Guid? first = network.PendingIntentId;

        var ex = Assert.Throws<BusinessException>(() => network.RequestDelete());

        Assert.Equal("NETWORK_INVALID_STATE", ex.Code);
        Assert.Equal(first, network.PendingIntentId);
    }

    [Fact]
    public void RequestDelete_WhenDeleted_Throws()
    {
        var network = NetworkIn(Seed.Deleted);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => network.RequestDelete()).Code);
    }

    [Fact]
    public void RequestDelete_OfAFailedCreateWithNoId_Throws_ItIsDeletedLocally()
    {
        var network = NetworkIn(Seed.CreateFailed);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => network.RequestDelete()).Code);
    }

    // ── Delete outcome ─────────────────────────────────────

    [Fact]
    public void MarkDeleted_FulfilsTheWaitingDelete_Once_AndClearsTheStatus()
    {
        var network = MakeActiveNetwork();
        network.RequestDelete();

        bool first = network.MarkDeleted();
        bool repeat = network.MarkDeleted();

        Assert.True(first);
        Assert.False(repeat);
        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.True(network.IsTerminal);
        Assert.Null(network.Transition);
    }

    // ── Terminal state absorbs (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#terminal-state-absorbs) ──

    [Fact]
    public void Deleted_IsAbsorbing_ForEveryLaterObservation()
    {
        var network = NetworkIn(Seed.Deleted);

        Assert.False(network.MarkObserved(NetworkStatus.Created));
        network.MarkNotFound();
        network.MarkCreated("other", "10.9.0.0/24", "10.9.0.1");
        network.MarkRequestFailed("late", "late");

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Null(network.CurrentState);
        Assert.Equal("cs-net-id", network.ExternalId);
        Assert.Null(network.RequestFailureReason);
    }

    // ── Rename ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Seed.DeleteRequested)]
    [InlineData(Seed.Deleted)]
    public void Rename_WhenBeingOrBeenDeleted_Throws(Seed seed)
    {
        var network = NetworkIn(seed);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(() => network.Rename("new")).Code);
    }

    // ── DeleteLocally ──────────────────────────────────────────────────────

    [Fact]
    public void DeleteLocally_OnAFailedCreateWithNoId_EndsDeleted()
    {
        var network = NetworkIn(Seed.CreateFailed);

        network.DeleteLocally();

        Assert.Equal(CurrentExistence.Deleted, network.CurrentExistence);
        Assert.Equal(DesiredExistence.Deleted, network.DesiredExistence);
        Assert.Null(network.Transition);
    }

    [Theory]
    [InlineData(Seed.Creating)]
    [InlineData(Seed.Active)]
    [InlineData(Seed.NotFound)]
    public void DeleteLocally_OnANetworkIncusMayHold_OrWhileItsCreateWaits_IsRefused(Seed seed)
    {
        var network = NetworkIn(seed);

        Assert.Equal("NETWORK_INVALID_STATE", Assert.Throws<BusinessException>(network.DeleteLocally).Code);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static Network MakeActiveNetwork()
    {
        var network = Network.CreateUnchecked(Guid.NewGuid(), "my-net", "zone-1");
        network.MarkActive("cs-net-id", "10.0.0.0/24", "10.0.0.1");
        return network;
    }
}
